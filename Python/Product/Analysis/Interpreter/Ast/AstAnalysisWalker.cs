﻿// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.PythonTools.Analysis;
using Microsoft.PythonTools.Analysis.Infrastructure;
using Microsoft.PythonTools.Parsing.Ast;

namespace Microsoft.PythonTools.Interpreter.Ast {
    class AstAnalysisWalker : PythonWalker {
        private readonly IPythonModule _module;
        private readonly Dictionary<string, IMember> _members;
        private readonly List<AstAnalysisFunctionWalker> _postWalkers = new List<AstAnalysisFunctionWalker>();
        private readonly AnalysisLogWriter _log;

        private IPythonInterpreter _interpreter => Scope.Interpreter;
        private PythonAst _ast => Scope.Ast;
        private IPythonType _unknownType => Scope._unknownType;

        public AstAnalysisWalker(
            IPythonInterpreter interpreter,
            PythonAst ast,
            IPythonModule module,
            string filePath,
            Uri documentUri,
            Dictionary<string, IMember> members,
            bool includeLocationInfo,
            bool warnAboutUndefinedValues,
            AnalysisLogWriter log = null
        ) {
            _log = log ?? (interpreter as AstPythonInterpreter)?._log;
            _module = module ?? throw new ArgumentNullException(nameof(module));
            _members = members ?? throw new ArgumentNullException(nameof(members));
            Scope = new NameLookupContext(
                interpreter ?? throw new ArgumentNullException(nameof(interpreter)),
                interpreter.CreateModuleContext(),
                ast ?? throw new ArgumentNullException(nameof(ast)),
                _module,
                filePath,
                documentUri,
                includeLocationInfo,
                log: warnAboutUndefinedValues ? _log : null
            );
            WarnAboutUndefinedValues = warnAboutUndefinedValues;
        }

        public bool CreateBuiltinTypes { get; set; }
        public bool WarnAboutUndefinedValues { get; }
        public NameLookupContext Scope { get; }

        public override bool Walk(PythonAst node) {
            if (_ast != node) {
                throw new InvalidOperationException("walking wrong AST");
            }

            FirstPassCollectClasses();
            Scope.PushScope(_members);

            return base.Walk(node);
        }

        public override void PostWalk(PythonAst node) {
            Scope.PopScope();
            base.PostWalk(node);
        }

        public void Complete() {
            foreach (var walker in _postWalkers) {
                walker.Walk();
            }
        }

        internal LocationInfo GetLoc(ClassDefinition node) {
            if (!Scope.IncludeLocationInfo) {
                return null;
            }
            if (node == null || node.StartIndex >= node.EndIndex) {
                return null;
            }

            var start = node.NameExpression?.GetStart(_ast) ?? node.GetStart(_ast);
            var end = node.GetEnd(_ast);
            return new LocationInfo(Scope.FilePath, Scope.DocumentUri, start.Line, start.Column, end.Line, end.Column);
        }

        internal LocationInfo GetLoc(Node node) => Scope.GetLoc(node);
        private IPythonType CurrentClass => Scope.GetInScope("__class__") as IPythonType;

        private IMember Clone(IMember member) 
            => member is IPythonMultipleMembers mm ? new AstPythonMultipleMembers(mm.Members) : member;

        public override bool Walk(AssignmentStatement node) {
            var value = Scope.GetValueFromExpression(node.Right);
            if ((value == null || value.MemberType == PythonMemberType.Unknown) && WarnAboutUndefinedValues) {
                _log?.Log(TraceLevel.Warning, "UndefinedValue", node.Right.ToCodeString(_ast).Trim());
            }
            if ((value as IPythonConstant)?.Type?.TypeId == BuiltinTypeId.Ellipsis) {
                value = _unknownType;
            }

            foreach (var expr in node.Left.OfType<ExpressionWithAnnotation>()) {
                if (expr.Expression is NameExpression ne) {
                    var annType = Scope.GetValueFromExpression(expr.Annotation) as IPythonType;
                    if (annType != null) {
                        Scope.SetInScope(ne.Name, new AstPythonConstant(annType, GetLoc(expr.Expression)));
                    } else {
                        Scope.SetInScope(ne.Name, _unknownType);
                    }
                }
            }

            foreach (var ne in node.Left.OfType<NameExpression>()) {
                Scope.SetInScope(ne.Name, Clone(value));
            }

            return base.Walk(node);
        }

        public override bool Walk(ImportStatement node) {
            if (node.Names == null) {
                return false;
            }

            try {
                for (int i = 0; i < node.Names.Count; ++i) {
                    var n = node.AsNames?[i] ?? node.Names[i].Names[0];
                    if (n != null) {
                        Scope.SetInScope(n.Name, new AstNestedPythonModule(
                            _interpreter,
                            n.Name,
                            PythonAnalyzer.ResolvePotentialModuleNames(_module.Name, Scope.FilePath, node.Names[i].MakeString(), true).ToArray()
                        ));
                    }
                }
            } catch (IndexOutOfRangeException) {
            }
            return false;
        }

        private static IEnumerable<KeyValuePair<string, NameExpression>> GetImportNames(IEnumerable<NameExpression> names, IEnumerable<NameExpression> asNames) {
            if (names == null) {
                return Enumerable.Empty<KeyValuePair<string, NameExpression>>();
            }
            if (asNames == null) {
                return names.Select(n => new KeyValuePair<string, NameExpression>(n.Name, n)).Where(k => !string.IsNullOrEmpty(k.Key));
            }
            return names
                .Zip(asNames.Concat(Enumerable.Repeat((NameExpression)null, int.MaxValue)),
                     (n1, n2) => new KeyValuePair<string, NameExpression>(n1?.Name, string.IsNullOrEmpty(n2?.Name) ? n1 : n2))
                .Where(k => !string.IsNullOrEmpty(k.Key));
        }

        public override bool Walk(FromImportStatement node) {
            var modName = node.Root.MakeString();
            if (modName == "__future__") {
                return false;
            }

            if (node.Names == null) {
                return false;
            }

            var mod = new AstNestedPythonModule(
                _interpreter,
                modName,
                PythonAnalyzer.ResolvePotentialModuleNames(_module.Name, Scope.FilePath, modName, true).ToArray()
            );

            foreach (var name in GetImportNames(node.Names, node.AsNames)) {
                if (name.Key == "*") {
                    mod.Imported(Scope.Context);
                    // Ensure child modules have been loaded
                    mod.GetChildrenModules();
                    foreach (var member in mod.GetMemberNames(Scope.Context)) {
                        var mem = mod.GetMember(Scope.Context, member) ??
                            new AstPythonConstant(_unknownType, mod.Locations.ToArray());
                        if (mem.MemberType == PythonMemberType.Unknown && WarnAboutUndefinedValues) {
                            _log?.Log(TraceLevel.Warning, "UndefinedImport", modName, name);
                        }
                        Scope.SetInScope(member, mem);
                        (mem as IPythonModule)?.Imported(Scope.Context);
                    }
                } else {
                    IMember mem;
                    if (mod.IsLoaded) {
                        mem = mod.GetMember(Scope.Context, name.Key) ??
                            new AstPythonConstant(_unknownType, GetLoc(name.Value));
                        if (mem.MemberType == PythonMemberType.Unknown && WarnAboutUndefinedValues) {
                            _log?.Log(TraceLevel.Warning, "UndefinedImport", modName, name);
                        }
                        (mem as IPythonModule)?.Imported(Scope.Context);
                    } else {
                        mem = new AstNestedPythonModuleMember(name.Key, mod, Scope.Context, GetLoc(name.Value));
                    }
                    Scope.SetInScope(name.Value.Name, mem);
                }
            }

            return false;
        }

        public override bool Walk(FunctionDefinition node) {
            if (node.IsLambda) {
                return false;
            }

            var dec = (node.Decorators?.DecoratorsInternal).MaybeEnumerate();
            if (dec.OfType<NameExpression>().Any(n => n.Name == "property")) {
                AddProperty(node);
                return false;
            }
            foreach (var setter in dec.OfType<MemberExpression>().Where(n => n.Name == "setter")) {
                if (setter.Target is NameExpression src) {
                    var existingProp = Scope.LookupNameInScopes(src.Name, NameLookupContext.LookupOptions.Local) as AstPythonProperty;
                    if (existingProp != null) {
                        // Setter for an existing property, so don't create a function
                        existingProp.MakeSettable();
                        return false;
                    }
                }
            }

            var existing = Scope.LookupNameInScopes(node.Name, NameLookupContext.LookupOptions.Local) as AstPythonFunction;

            if (existing == null) {
                existing = new AstPythonFunction(_ast, _module, CurrentClass, node, GetLoc(node));
                Scope.SetInScope(node.Name, existing);
            }

            var funcScope = Scope.Clone();
            if (CreateBuiltinTypes) {
                funcScope.SuppressBuiltinLookup = true;
            }
            existing.AddOverload(CreateFunctionOverload(funcScope, node));

            // Do not recurse into functions
            return false;
        }

        private void AddProperty(FunctionDefinition node) {
            var existing = Scope.LookupNameInScopes(node.Name, NameLookupContext.LookupOptions.Local) as AstPythonProperty;

            if (existing == null) {
                existing = new AstPythonProperty(_ast, node, GetLoc(node));
                Scope.SetInScope(node.Name, existing);
            }

            // Treat the rest of the property as a function. "AddOverload" takes the return type
            // and sets it as the property type.
            var funcScope = Scope.Clone();
            if (CreateBuiltinTypes) {
                funcScope.SuppressBuiltinLookup = true;
            }
            existing.AddOverload(CreateFunctionOverload(funcScope, node));
        }

        private IPythonFunctionOverload CreateFunctionOverload(NameLookupContext funcScope, FunctionDefinition node) {
            var parameters = new List<AstPythonParameterInfo>();
            foreach (var p in node.ParametersInternal) {
                var annType = p.Annotation != null ? Scope.GetValueFromExpression(p.Annotation) as IPythonType : null;
                parameters.Add(new AstPythonParameterInfo(_ast, p, annType != null ? new[] { annType } : null));
            }

            var overload = new AstPythonFunctionOverload(parameters, funcScope.GetLocOfName(node, node.NameExpression));

            var funcWalk = new AstAnalysisFunctionWalker(funcScope, node, overload);
            _postWalkers.Add(funcWalk);

            return overload;
        }

        private static string GetDoc(SuiteStatement node) {
            var docExpr = node?.Statements?.FirstOrDefault() as ExpressionStatement;
            var ce = docExpr?.Expression as ConstantExpression;
            return ce?.Value as string;
        }

        private AstPythonType CreateType(ClassDefinition node) {
            if (node == null) {
                throw new ArgumentNullException(nameof(node));
            }
            if (CreateBuiltinTypes) {
                return new AstPythonBuiltinType(_ast, _module, node, GetDoc(node.Body as SuiteStatement), GetLoc(node));
            }

            return new AstPythonType(_ast, _module, node, GetDoc(node.Body as SuiteStatement), GetLoc(node));
        }

        public void FirstPassCollectClasses() {
            foreach (var node in (_ast.Body as SuiteStatement).Statements.OfType<ClassDefinition>()) {
                _members[node.Name] = CreateType(node);
            }
            foreach (var node in (_ast.Body as SuiteStatement).Statements.OfType<AssignmentStatement>()) {
                var rhs = node.Right as NameExpression;
                if (rhs == null) {
                    continue;
                }

                if (_members.TryGetValue(rhs.Name, out var member)) {
                    foreach (var lhs in node.Left.OfType<NameExpression>()) {
                        _members[lhs.Name] = member;
                    }
                }
            }
        }

        public override bool Walk(ClassDefinition node) {
            var member = Scope.GetInScope(node.Name);
            AstPythonType t = member as AstPythonType;
            if (t == null && member is IPythonMultipleMembers mm) {
                t = mm.Members.OfType<AstPythonType>().FirstOrDefault(pt => pt.StartIndex == node.StartIndex);
            }
            if (t == null) {
                t = CreateType(node);
                Scope.SetInScope(node.Name, t);
            }

            if (t.Bases == null) {
                var bases = node.BasesInternal.Where(a => string.IsNullOrEmpty(a.Name))
                    .Select(a => Scope.GetValueFromExpression(a.Expression))
                    .OfType<IPythonType>()
                    .ToArray();

                try {
                    t.SetBases(_interpreter, bases);
                } catch (InvalidOperationException) {
                    // Bases were set while we were working
                }
            }

            Scope.PushScope();
            Scope.SetInScope("__class__", t);

            return true;
        }

        public override void PostWalk(ClassDefinition node) {
            var cls = CurrentClass as AstPythonType;
            var m = Scope.PopScope();
            if (cls != null && m != null) {
                cls.AddMembers(m, true);
            }
            base.PostWalk(node);
        }
    }
}
