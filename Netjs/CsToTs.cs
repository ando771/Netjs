﻿// Copyright 2014 Frank A. Krueger
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//    http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.Decompiler.Ast.Transforms;
using ICSharpCode.NRefactory.CSharp;
using ICSharpCode.NRefactory.TypeSystem;
using Mono.Cecil;
using ICSharpCode.NRefactory;

namespace Netjs
{
	public class CsToTs : IAstTransform
	{
		public void Run (AstNode compilationUnit)
		{
			foreach (var t in GetTransforms ()) {
				t.Run (compilationUnit);
			}
		}

		IEnumerable<IAstTransform> GetTransforms ()
		{
			yield return new FixBadNames ();
			yield return new LiftNestedClasses ();
			yield return new RemoveConstraints ();
			yield return new FlattenNamespaces ();
			yield return new StructToClass ();
			yield return new FixGenericsThatUseObject ();
			yield return new RemoveNonGenericEnumerable ();
			yield return new RemovePrivateInterfaceOverloads ();
			yield return new AddAbstractMethodBodies ();
			yield return new MergeCtors ();
			yield return new EnsureAtLeastOneCtor ();
			yield return new DealWithStaticCtors ();
			yield return new MakeSuperCtorFirst ();
			yield return new MergeOverloads ();
			yield return new PropertiesToMethods ();
			yield return new FixCatches ();
			yield return new FixEmptyThrow ();
			yield return new AnonymousInitializersNeedNames ();
			yield return new FixEvents ();
			yield return new InlineEnumMethods ();
			yield return new InlineDelegates ();
			yield return new OperatorDeclsToMethods ();
			yield return new PassArraysAsEnumerables ();
			yield return new WrapRefArgs ();
			yield return new ReplaceDefault ();
			yield return new IndexersToMethods ();
			yield return new ReplaceInstanceMembers ();
			yield return new CharsToNumbers ();
			yield return new StringConstructorsToMethods ();
			yield return new MakePrimitiveTypesJsTypes ();
			yield return new FixIsOp ();
			yield return new ExpandOperators ();
			yield return new ExpandIndexers ();
			yield return new Renames ();
			yield return new SuperPropertiesToThis ();
			yield return new BitwiseOrToConditionalForBooleans ();
			yield return new RemoveDelegateConstructors ();
			yield return new RemoveNullable ();
			yield return new RemoveEnumBaseType ();
			yield return new RemoveGenericArgsInIsExpr ();
			yield return new RemoveAttributes ();
			yield return new RemoveModifiers ();
			yield return new RemoveEmptySwitch ();
			yield return new MakeWhileLoop ();
			yield return new GotoRemoval ();
			yield return new AddReferences ();
		}

		class MakeWhileLoop : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitLabelStatement (LabelStatement labelStatement)
			{
				base.VisitLabelStatement (labelStatement);

				var ifs = labelStatement.NextSibling as IfElseStatement;
				if (ifs == null || !ifs.FalseStatement.IsNull)
					return;

				var b = ifs.TrueStatement as BlockStatement;
				if (b == null || b.Statements.Count == 0)
					return;

				var gt = b.Statements.Last () as GotoStatement;
				if (gt == null || gt.Label != labelStatement.Label)
					return;

				if (labelStatement.GetParent<MethodDeclaration> ().Descendants.OfType<GotoStatement> ().Count () != 1)
					return;

				gt.Remove ();
				b.Remove ();
				var wh = new WhileStatement {
					Condition = ifs.Condition.Clone (),
					EmbeddedStatement = b,
				};

				ifs.ReplaceWith (wh);

				labelStatement.Remove ();

			}
		}

		class RemoveEmptySwitch : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitSwitchStatement (SwitchStatement switchStatement)
			{
				base.VisitSwitchStatement (switchStatement);

				if (switchStatement.SwitchSections.Count > 0)
					return;
				if (!(switchStatement.Expression is IdentifierExpression))
					return;

				switchStatement.Remove ();
			}
		}


		class GotoRemoval : DepthFirstAstVisitor, IAstTransform
		{
			static int nextId = 1;

			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (new MoveLabelsOutOfTry ());
				compilationUnit.AcceptVisitor (this);
			}

			// OMG OMG HACK: Goto removal is a deep dark problem that I continually fail
			// at solving. So here is the trick: we transform the method until it's in a 
			// simple form whose gotos can be replicated with a JS labelled loop.
			// Problem is, those transforms are very finicky. Some work well on some methods
			// while causing problems on others. So we try a variety of transforms hoping that
			// at least one set will work.
			readonly IAstVisitor[][] transforms = {
				new IAstVisitor[] { },
				new IAstVisitor[] { new LiftLabeledSwitchSections () },
				new IAstVisitor[] { new LiftLabeledSwitchSections (), new InlineGoto () },
				new IAstVisitor[] { new AddImplicitGoto (), new InlineGoto () },
			};

			public override void VisitMethodDeclaration (MethodDeclaration methodDeclaration)
			{
				base.VisitMethodDeclaration (methodDeclaration);

				if (methodDeclaration.Body.IsNull)
					return;

				//
				// Try to force the method into a form we can handle
				//
				var m = methodDeclaration;

				var gotos = m.Body.Descendants.OfType<GotoStatement> ().ToList ();
				if (gotos.Count == 0)
					return;

				if (HasBadLabels (m)) {
					foreach (var ts in transforms) {
						m = (MethodDeclaration)methodDeclaration.Clone ();

						m.AcceptVisitor (new LabelLoops ());
						m.AcceptVisitor (new SwitchSectionBlocksToStatements ());
						foreach (var t in ts) {
							m.AcceptVisitor (t);
						}
						m.AcceptVisitor (new RemoveRedundantGotos ());
						m.AcceptVisitor (new RemoveUnreachableStatements ());
						m.AcceptVisitor (new RemoveRedundantGotos ()); // again :-(
						m.AcceptVisitor (new RemoveLabelsWithoutGotos ());

						if (!HasBadLabels (m)) {
							break;
						}
					}

					if (HasBadLabels (m)) {
						Console.WriteLine ("There are goto labels without the same parent. This is not supported.");
						return;
					} else {
						methodDeclaration.ReplaceWith (m);
					}
				}

				//
				// Handle it
				//
				m.AcceptVisitor (new CreateGotoLoop ());
				m.AcceptVisitor (new RewriteGotos ());
			}

			class MoveLabelsOutOfTry : DepthFirstAstVisitor
			{
				public override void VisitLabelStatement (LabelStatement labelStatement)
				{
					base.VisitLabelStatement (labelStatement);

					var t = labelStatement.GetParent <TryCatchStatement> ();
					if (t == null)
						return;

					labelStatement.Remove ();
					t.Parent.InsertChildBefore (t, labelStatement, (Role<Statement>)t.Role);
				}
			}

			class RewriteGotos : DepthFirstAstVisitor
			{
				public override void VisitGotoStatement (GotoStatement gotoStatement)
				{
					base.VisitGotoStatement (gotoStatement);

					var a = new ExpressionStatement (
						new AssignmentExpression (
							new IdentifierExpression ("_goto"), 
							new IdentifierExpression (gotoStatement.Label)));

					var c = new ContinueStatement ();
					c.AddAnnotation (new LabelStatement { Label = "_GOTO_LOOP" });

					gotoStatement.ReplaceWith (c);
					c.Parent.InsertChildBefore (c, a, (Role<Statement>)c.Role);
				}
			}

			class CreateGotoLoop : DepthFirstAstVisitor
			{
				public override void VisitMethodDeclaration (MethodDeclaration methodDeclaration)
				{
					base.VisitMethodDeclaration (methodDeclaration);

					var gotos = methodDeclaration.Body.Descendants.OfType<GotoStatement> ().ToList ();
					if (gotos.Count == 0)
						return;

					var loop = new WhileStatement {
						Condition = new PrimitiveExpression (true),
					};
					var loopBlock = new BlockStatement ();
					var loopSwitch = new SwitchStatement {
						Expression = new IdentifierExpression ("_goto"),
					};
					var loopLabel = new LabelStatement {
						Label = "_GOTO_LOOP",
					};
					loopBlock.Statements.Add (loopSwitch);
					loop.EmbeddedStatement = loopBlock;

					var firstLabel = methodDeclaration.Body.Descendants.First (x => /*(x is GotoStatement) ||*/ (x is LabelStatement && HasGoto ((LabelStatement)x)));

					var block = firstLabel.Parent;

					var labels = new List<Tuple<LabelStatement, List<AstNode>>> ();
					labels.Add (new Tuple<LabelStatement, List<AstNode>> (null, new List<AstNode> ()));

					var n = firstLabel.Parent.FirstChild;
					while (n != null && !n.IsNull) {

						var l = n as LabelStatement;
						if (l != null && gotos.Any (x => x.Label == l.Label)) {
							labels.Add (new Tuple<LabelStatement, List<AstNode>> (l, new List<AstNode> ()));
						} else {
							labels.Last ().Item2.Add (n);
						}

						var s = n.NextSibling;
						n.Remove ();
						n = s;
					}

					for (int i = 0; i < labels.Count; i++) {
						var ls = labels [i];

						var sec = new SwitchSection ();
						sec.CaseLabels.Add (ls.Item1 != null ? new CaseLabel (new PrimitiveExpression (i)) : new CaseLabel ());


						if (ls.Item2.Count == 0 || !StatementIsBranch (ls.Item2.Last ())) {
							if (i + 1 < labels.Count) {
								ls.Item2.Add (new GotoStatement (labels [i + 1].Item1.Label));
							} else {
								var br = new BreakStatement ();
								br.AddAnnotation (new LabelStatement { Label = "_GOTO_LOOP" });
								ls.Item2.Add (br);
							}

						}

						sec.Statements.AddRange (ls.Item2.OfType<Statement> ());
						loopSwitch.SwitchSections.Add (sec);

						if (ls.Item1 != null) {
							loopBlock.Statements.InsertBefore (
								loopSwitch, 
								new VariableDeclarationStatement (new PrimitiveType ("number"), ls.Item1.Label, new PrimitiveExpression (i)));
						}
					}

					loopBlock.Statements.InsertBefore (
						loopSwitch, 
						new VariableDeclarationStatement (new PrimitiveType ("number"), "_goto", new PrimitiveExpression (0)));

					block.AddChild (loopLabel, (Role<Statement>)firstLabel.Role);
					block.AddChild (loop, (Role<Statement>)firstLabel.Role);
				}
			}

			static bool HasBadLabels (MethodDeclaration methodDeclaration)
			{
				var labels = methodDeclaration.Body.Descendants.OfType<LabelStatement> ().Where (HasGoto).ToList ();
				var labelsParent = labels [0].Parent;
				return labels.Any (x => x.Parent != labelsParent);
			}

			class AddImplicitGoto : DepthFirstAstVisitor
			{
				public override void VisitLabelStatement (LabelStatement labelStatement)
				{
					base.VisitLabelStatement (labelStatement);

					var safes = GetSafesToEnd (labelStatement);

					if (safes.Count < 1)
						return;

					if (StatementIsBranch (safes.Last ()))
						return;

					var n = StatementGetNextStatement ((Statement)safes.Last ()) as LabelStatement;

					if (n == null)
						return;

					labelStatement.Parent.InsertChildAfter (safes.Last (), new GotoStatement (n.Label), (Role<Statement>)labelStatement.Role);
				}
			}

			static Statement StatementGetNextStatement (Statement start)
			{
				return start.GetNextNode() as Statement;
			}

			static IfElseStatement GetParentIf (AstNode node)
			{
				var p = node.Parent;
				while (p != null && !p.IsNull) {
					var pif = p as IfElseStatement;
					if (pif != null)
						return pif;
					p = p.Parent;
				}
				return null;
			}

			class SwitchSectionBlocksToStatements : DepthFirstAstVisitor
			{
				public override void VisitSwitchSection (SwitchSection switchSection)
				{
					base.VisitSwitchSection (switchSection);

					if (switchSection.Statements.Count != 1)
						return;

					var block = switchSection.Statements.First () as BlockStatement;
					if (block == null)
						return;

					foreach (var s in block.Statements.ToList ()) {
						s.Remove ();
						switchSection.Statements.Add (s);
					}

					block.Remove ();
				}
			}

			class RemoveLabelsWithoutGotos : DepthFirstAstVisitor
			{
				public override void VisitLabelStatement (LabelStatement labelStatement)
				{
					base.VisitLabelStatement (labelStatement);


					if (HasGoto (labelStatement) || HasBreakto (labelStatement)) {
						return;
					}

					labelStatement.Remove ();

				}
			}

			class RemoveRedundantGotos : DepthFirstAstVisitor
			{
				public override void VisitGotoStatement (GotoStatement gotoStatement)
				{
					base.VisitGotoStatement (gotoStatement);

					var label = gotoStatement.NextSibling as LabelStatement;
					if (label != null && label.Label == gotoStatement.Label)
						gotoStatement.Remove ();

				}

				public override void VisitSwitchStatement (SwitchStatement switchStatement)
				{
					base.VisitSwitchStatement (switchStatement);

					if (switchStatement.SwitchSections.Count == 0)
						return;

					var last = switchStatement.SwitchSections.Last ();
					if (last.Statements.Count != 1)
						return;


					var trailingLabel = switchStatement.NextSibling as LabelStatement;
					if (trailingLabel == null)
						return;


					if (last.Statements.Count == 1 && last.Statements.First () is GotoStatement
						&& ((GotoStatement)last.Statements.First ()).Label == trailingLabel.Label) {

						last.Remove ();

					}
				}


			}

			class RemoveUnreachableStatements : DepthFirstAstVisitor
			{
				public override void VisitGotoStatement (GotoStatement gotoStatement)
				{
					base.VisitGotoStatement (gotoStatement);

					while (gotoStatement.NextSibling != null && !gotoStatement.NextSibling.IsNull && !(gotoStatement.NextSibling is LabelStatement)) {
						gotoStatement.NextSibling.Remove ();
					}
				}

				public override void VisitReturnStatement (ReturnStatement returnStatement)
				{
					base.VisitReturnStatement (returnStatement);

					while (returnStatement.NextSibling != null && !returnStatement.NextSibling.IsNull && !(returnStatement.NextSibling is LabelStatement)) {
						returnStatement.NextSibling.Remove ();
					}
				}


			}

			static bool HasGoto (LabelStatement label)
			{
				var m = label.GetParent<MethodDeclaration> ();
				if (m == null)
					return false;

				return m.Descendants.OfType<GotoStatement> ().Any (x => x.Label == label.Label);
			}

			static bool HasBreakto (LabelStatement label)
			{
				var m = label.GetParent<MethodDeclaration> ();
				if (m == null)
					return false;

				return m.Descendants.OfType<BreakStatement> ().Any (x => {
					var l = x.Annotation<LabelStatement> ();
					if (l == null)
						return false;
					return l.Label == label.Label;
				});
			}


			class LabelLoops : DepthFirstAstVisitor
			{
				public override void VisitBreakStatement (BreakStatement breakStatement)
				{
					base.VisitBreakStatement (breakStatement);

					var loop = GetOuterLoop (breakStatement);
					if (loop == null || loop is SwitchStatement)
						return;

					var label = GetOrAddStatementLabel (loop, "loop");

					breakStatement.AddAnnotation (label);
				}
			}

			static Statement GetOuterLoop (Statement s)
			{
				AstNode l = s;
				while (l != null && !l.IsNull && !(l is ForStatement || l is WhileStatement || l is DoWhileStatement || l is SwitchStatement)) {
					l = l.Parent;
				}
				return l as Statement;
			}

			static LabelStatement GetOrAddStatementLabel (Statement s, string name)
			{
				var label = GetStatementLabel (s);
				if (label == null) {
					label = AddStatementLabel (s, name);
				}
				return label;
			}

			static LabelStatement GetStatementLabel (Statement s)
			{
				return s.PrevSibling as LabelStatement;
			}

			static LabelStatement AddStatementLabel (Statement s, string name)
			{
				var l = new LabelStatement {
					Label = "_" + name + (nextId++),
				};
				s.Parent.InsertChildBefore (s, l, (ICSharpCode.NRefactory.Role<Statement>)s.Role);
				return l;
			}

			class InlineGoto : DepthFirstAstVisitor
			{
				public override void VisitMethodDeclaration (MethodDeclaration methodDeclaration)
				{
					base.VisitMethodDeclaration (methodDeclaration);

					var inlabels = methodDeclaration.Body.Descendants.OfType<LabelStatement> ().Select (LabelIsInlineable).Where (x => x.Item2.Count > 0).ToList ();

					if (inlabels.Count == 0)
						return;

					foreach (var l in inlabels) {
						var labelName = l.Item1.Label;
						foreach (var g in methodDeclaration.Body.Descendants.OfType<GotoStatement> ().Where (x => x.Label == labelName)) {

							foreach (var n in l.Item2) {
								g.Parent.InsertChildBefore (g, (Statement)n.Clone (), (Role<Statement>)g.Role);
							}

							g.Remove ();
						}

						l.Item1.Remove ();
					}
				}
			}

			static List<AstNode> GetSafesToEnd (LabelStatement label)
			{
				var safes = new List<AstNode> ();

				var s = label.NextSibling;
				do {
					if (StatementIsBranch (s)) {
						safes.Add (s);
						return safes;
					} else if (StatementIsSafe (s)) {
						safes.Add (s);
						s = s.NextSibling;
					} else {
						safes.Clear ();
						return safes;
					}
				} while (s != null && !s.IsNull);

				return safes;
			}

			static Tuple<LabelStatement, List<AstNode>> LabelIsInlineable (LabelStatement label)
			{
				var safes = new List<AstNode> ();

				var s = label.NextSibling;
				do {
					if (StatementIsBranch (s)) {
						safes.Add (s);
						return Tuple.Create (label, safes);
					} else if (StatementIsSafe (s)) {
						safes.Add (s);
						s = s.NextSibling;
					} else {
						safes.Clear ();
						return Tuple.Create (label, safes);
					}
				} while (s != null && !s.IsNull);

				safes.Clear ();
				return Tuple.Create (label, safes);
			}

			static bool StatementIsSafe (AstNode n)
			{
				return !(n is LabelStatement || n is SwitchStatement);
			}

			static bool StatementIsBranch (AstNode n)
			{
				if (n is ThrowStatement || n is GotoStatement || n is ReturnStatement)
					return true;
				var b = n as BreakStatement;
				if (b != null) {
					var blabel = b.Annotation<LabelStatement> ();
					if (blabel != null)
						return true;
				}
				return false;
			}

			class AddGotoLabeledSwitchSections : DepthFirstAstVisitor
			{
				public override void VisitSwitchStatement (SwitchStatement switchStatement)
				{
					base.VisitSwitchStatement (switchStatement);

					var labelledSections = switchStatement.SwitchSections.Where (x => x.Statements.Count > 0 && x.Statements.First () is LabelStatement).ToList ();

					if (labelledSections.Count == 0)
						return;

					//
					// Make sure the statement after the switch is labelled
					// so that we can branch to it
					//
					var endLabel = switchStatement.NextSibling as LabelStatement;
					if (endLabel == null) {
						endLabel = new LabelStatement {
							Label = "_SwitchEnd" + nextId++
						};
						switchStatement.Parent.InsertChildAfter (
							switchStatement,
							endLabel,
							(ICSharpCode.NRefactory.Role<Statement>)switchStatement.Role);
					}

					foreach (var ls in labelledSections) {

						var last = ls.Statements.Last ();
						if (last is BreakStatement) {
							last.ReplaceWith (new GotoStatement (endLabel.Label));
						}

					}
				}
			}

			class LiftLabeledSwitchSections : DepthFirstAstVisitor
			{
				public override void VisitSwitchStatement (SwitchStatement switchStatement)
				{
					base.VisitSwitchStatement (switchStatement);

					var labelledSections = switchStatement.SwitchSections.Where (x => x.Statements.Count > 0 && x.Statements.First () is LabelStatement).ToList ();

					if (labelledSections.Count == 0)
						return;

					//
					// Make sure the statement after the switch is labelled
					// so that we can branch to it
					//
					var endLabel = switchStatement.NextSibling as LabelStatement;
					if (endLabel == null) {
						endLabel = new LabelStatement {
							Label = "_SwitchEnd" + nextId++
						};
						switchStatement.Parent.InsertChildAfter (
							switchStatement,
							endLabel,
							(ICSharpCode.NRefactory.Role<Statement>)switchStatement.Role);
					}

					foreach (var ls in labelledSections) {

						//
						// Move labeled sections out of the switch
						//

						var label = (LabelStatement)ls.Statements.First ();


						AstNode p = switchStatement;
						foreach (var s in ls.Statements) {

							var br = s as BreakStatement;

							var ns = br != null ? new GotoStatement (endLabel.Label) : s.Clone ();
							switchStatement.Parent.InsertChildAfter (
								p,
								ns,
								(Role<Statement>)p.Role);
							p = ns;
						}

						//
						// Have the switch goto that label
						//
						ls.Statements.Clear ();
						ls.Statements.Add (new GotoStatement (label.Label));

					}

					//
					// Remind the switch to run its next statement
					//
					if (endLabel != switchStatement.NextSibling) {
						switchStatement.Parent.InsertChildAfter (
							switchStatement,
							new GotoStatement (endLabel.Label),
							(Role<Statement>)switchStatement.Role);
					}
				}
			}
		}

		class PassArraysAsEnumerables : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitInvocationExpression (InvocationExpression invocationExpression)
			{
				base.VisitInvocationExpression (invocationExpression);

				var md = GetMethodDef (invocationExpression);
				if (md == null) {
					return;
				}

				var i = 0;
				foreach (var a in invocationExpression.Arguments) {

					var p = md.Parameters[i];
					i++;

					var tra = GetTypeRef (a);
					if (tra == null || !tra.IsArray) {
						continue;
					}

					var trp = p.ParameterType;

					if (!trp.Name.StartsWith ("IEnumerable", StringComparison.Ordinal)) {
						continue;
					}

					var wrap = new InvocationExpression (
						new MemberReferenceExpression (new TypeReferenceExpression (new SimpleType ("NArray")), "ToEnumerable"),
						a.Clone ());

					a.ReplaceWith (wrap);

				}

			}

			public override void VisitVariableInitializer (VariableInitializer variableInitializer)
			{
				base.VisitVariableInitializer (variableInitializer);

				var tra = GetTypeRef (variableInitializer.Initializer);
				if (tra == null || !tra.IsArray)
					return;

				var f = variableInitializer.Parent as VariableDeclarationStatement;
				if (f == null)
					return;

				var st = f.Type as SimpleType;
				if (st == null || !st.Identifier.StartsWith ("IEnumerable", StringComparison.Ordinal))
					return;

				var wrap = new InvocationExpression (
					new MemberReferenceExpression (new TypeReferenceExpression (new SimpleType ("NArray")), "ToEnumerable"),
					variableInitializer.Initializer.Clone ());

				variableInitializer.Initializer = wrap;
			}
		}

		class InlineEnumMethods : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitInvocationExpression (InvocationExpression invocationExpression)
			{
				base.VisitInvocationExpression (invocationExpression);

				var mr = invocationExpression.Target as MemberReferenceExpression;
				if (mr == null)
					return;

				var target = mr.Target;
				var td = GetTypeDef (target);
				if (td == null || !td.IsEnum)
					return;

				if (mr.MemberName == "GetHashCode") {

					target.Remove ();
					invocationExpression.ReplaceWith (target);

				}
				else if (mr.MemberName == "ToString") {

					target.Remove ();
					var idx = new IndexerExpression (
						          new TypeReferenceExpression (new SimpleType (td.Name)),
						          target);
					invocationExpression.ReplaceWith (idx);

				}
			}
		}

		class StringConstructorsToMethods : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitObjectCreateExpression (ObjectCreateExpression objectCreateExpression)
			{
				base.VisitObjectCreateExpression (objectCreateExpression);

				if (!IsString (objectCreateExpression.Type))
					return;

				var m = new InvocationExpression (
					new MemberReferenceExpression (new TypeReferenceExpression (new SimpleType ("NString")), "FromChars"),
					        objectCreateExpression.Arguments.Select (x => x.Clone ()));

				objectCreateExpression.ReplaceWith (m);
			}
		}

		static bool IsString (AstType type)
		{
			var pt = type as PrimitiveType;
			if (pt != null && pt.KnownTypeCode == KnownTypeCode.String)
				return true;

			var tr = GetTypeRef (type);
			return (tr != null && tr.FullName == "System.String");
		}

		class EnsureAtLeastOneCtor : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitTypeDeclaration (TypeDeclaration typeDeclaration)
			{
				base.VisitTypeDeclaration (typeDeclaration);

				if (typeDeclaration.ClassType != ClassType.Class)
					return;

				if (typeDeclaration.Members.OfType<ConstructorDeclaration> ().Any ())
					return;

				var ctor = new ConstructorDeclaration ();
				ctor.Name = "constructor";
				ctor.Body = new BlockStatement ();
				ctor.Body.Add (new InvocationExpression (new BaseReferenceExpression ()));
				typeDeclaration.Members.Add (ctor);
			}
		}

		class CharsToNumbers : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitPrimitiveExpression (PrimitiveExpression primitiveExpression)
			{
				base.VisitPrimitiveExpression (primitiveExpression);

				if (primitiveExpression.Value is char) {

					var ch = (char)primitiveExpression.Value;

					var sch = ch.ToString ();
					if (ch == '\n')
						sch = "\\n";
					if (ch == '\r')
						sch = "\\r";
					if (ch == '\t')
						sch = "\\t";

					primitiveExpression.Value = (int)ch;

					primitiveExpression.Parent.InsertChildAfter (primitiveExpression, new Comment ("'"+sch+"'", CommentType.MultiLine), Roles.Comment);

				}
			}

			public override void VisitIndexerExpression (IndexerExpression indexerExpression)
			{
				base.VisitIndexerExpression (indexerExpression);

				var t = GetTypeRef (indexerExpression.Target);

				if (t != null && t.FullName == "System.String") {


					var targ = indexerExpression.Target;
					targ.Remove ();
					var index = indexerExpression.Arguments.FirstOrNullObject ();
					index.Remove ();

					var i = new InvocationExpression (
						        new MemberReferenceExpression (targ, "charCodeAt"),
						        index);

					indexerExpression.ReplaceWith (i);
				}
			}
		}

		class MakeSuperCtorFirst : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitConstructorDeclaration (ConstructorDeclaration constructorDeclaration)
			{
				base.VisitConstructorDeclaration (constructorDeclaration);

				if (constructorDeclaration.Body.IsNull)
					return;

				var hasInits = constructorDeclaration.GetParent<TypeDeclaration> ().Members.OfType<FieldDeclaration> ().Any (x => x.Variables.Any (y => !y.Initializer.IsNull));
				if (!hasInits)
					return;

				var supers = constructorDeclaration.Body.Descendants.OfType<InvocationExpression> ().Where (
					x => x.Target is BaseReferenceExpression).Select (x => x.GetParent<Statement> ()).ToList ();

				foreach (var s in supers) {
					s.Remove ();
				}

				if (supers.Count > 0) {
					constructorDeclaration.Body.Statements.InsertBefore (
						constructorDeclaration.Body.Statements.FirstOrNullObject (),
						supers [0]);
				}
			}
		}

		class FixGenericsThatUseObject : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitMethodDeclaration (MethodDeclaration methodDeclaration)
			{
				base.VisitMethodDeclaration (methodDeclaration);

				if (methodDeclaration.TypeParameters.Count == 0)
					return;

				var q = from mr in methodDeclaration.Body.Descendants.OfType<MemberReferenceExpression> ()
				        where mr.MemberName == "Equals" || mr.MemberName == "ToString" || mr.MemberName == "GetHashCode"
						let tr = GetTypeRef (mr.Target)
						where tr != null && tr.IsGenericParameter
						select tr;

				var ms = q.ToList ();
				if (ms.Count == 0)
					return;


				foreach (var m in ms) {

					var c = new Constraint {
						TypeParameter = new SimpleType (m.Name)
					};
					c.BaseTypes.Add (new SimpleType ("NObject"));
					methodDeclaration.Constraints.Add (c);

				}
			}
		}

		class RemoveNonGenericEnumerable : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}


			public override void VisitTypeDeclaration (TypeDeclaration typeDeclaration)
			{
				base.VisitTypeDeclaration (typeDeclaration);

				foreach (var n in typeDeclaration.BaseTypes.Where (x => IsType (x, "IEnumerable") || IsType (x, "IEnumerator"))) {
					n.Remove ();
				}


				foreach (var n in typeDeclaration.Members.OfType<MethodDeclaration> ().Where (x => IsType (x.PrivateImplementationType, "IEnumerable") || IsType (x.PrivateImplementationType, "IEnumerator"))) {
					n.Remove ();
				}

				foreach (var n in typeDeclaration.Members.OfType<PropertyDeclaration> ().Where (x => IsType (x.PrivateImplementationType, "IEnumerable") || IsType (x.PrivateImplementationType, "IEnumerator"))) {
					n.Remove ();
				}



			}

			static bool IsType (AstType type, string typeName)
			{
				var st = type as SimpleType;
				return st != null && st.Identifier == typeName && st.TypeArguments.Count == 0;
			}
		}

		class RemovePrivateInterfaceOverloads : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitTypeDeclaration (TypeDeclaration typeDeclaration)
			{
				base.VisitTypeDeclaration (typeDeclaration);


				var mgs = typeDeclaration.Members.OfType<MethodDeclaration> ().GroupBy (x => x.Name);

				foreach (var mse in mgs) {

					var ms = mse.ToList ();

					var hasNorms = ms.Any (x => x.PrivateImplementationType.IsNull);
					var hasPrivs = ms.Any (x => !x.PrivateImplementationType.IsNull);

					if (hasPrivs) {
						if (hasNorms) {
							//
							// If we already have a public def, kill these private ones
							//
							foreach (var m in ms.Where (x => !x.PrivateImplementationType.IsNull)) {
								m.Remove ();
							}

						} else {
							foreach (var m in ms.Where (x => !x.PrivateImplementationType.IsNull)) {
								m.PrivateImplementationType.Remove ();
							}
						}
					}
				}
			}
		}

		class RemoveGenericArgsInIsExpr : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitIsExpression (IsExpression isExpression)
			{
				base.VisitIsExpression (isExpression);

				var st = isExpression.Type as SimpleType;
				if (st != null && st.TypeArguments.Count > 0) {

					var nt = (SimpleType)st.Clone ();
					nt.TypeArguments.Clear ();
				}
			}
		}

		class RemoveDelegateConstructors : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitObjectCreateExpression (ObjectCreateExpression objectCreateExpression)
			{
				base.VisitObjectCreateExpression (objectCreateExpression);

				var td = GetTypeDef (objectCreateExpression.Type);
				if (td == null || !IsDelegate (td) || objectCreateExpression.Arguments.Count != 1)
					return;

				var a = objectCreateExpression.Arguments.First ();
				a.Remove ();

				objectCreateExpression.ReplaceWith (a);
			}

			public override void VisitAssignmentExpression (AssignmentExpression assignmentExpression)
			{
				base.VisitAssignmentExpression (assignmentExpression);

				if (assignmentExpression.Operator != AssignmentOperatorType.Add && assignmentExpression.Operator != AssignmentOperatorType.Subtract)
					return;

				var t = assignmentExpression.Left.Annotation<EventDefinition> ();
				if (t == null)
					return;

				var left = assignmentExpression.Left;
				var right = assignmentExpression.Right;
				left.Remove ();
				right.Remove ();

				var isAdd = assignmentExpression.Operator == AssignmentOperatorType.Add;

				var m = new InvocationExpression (
					new MemberReferenceExpression (left, isAdd ? "Add" : "Remove"),
					        right);

				assignmentExpression.ReplaceWith (m);
			}
		}

		class BitwiseOrToConditionalForBooleans : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitBinaryOperatorExpression (BinaryOperatorExpression binaryOperatorExpression)
			{
				base.VisitBinaryOperatorExpression (binaryOperatorExpression);

				if (binaryOperatorExpression.Operator != BinaryOperatorType.BitwiseOr)
					return;

				var leftT = GetTypeRef (binaryOperatorExpression.Left);

				if (leftT != null && leftT.FullName == "System.Boolean") {
					binaryOperatorExpression.Operator = BinaryOperatorType.ConditionalOr;
				}
			}
		}

		class ReplaceInstanceMembers : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitMemberReferenceExpression (MemberReferenceExpression memberReferenceExpression)
			{
				base.VisitMemberReferenceExpression (memberReferenceExpression);

				var tre = memberReferenceExpression.Target as TypeReferenceExpression;

				if (tre != null) {

					var p = tre.Type as PrimitiveType;

					if (p != null) {
						if (p.KnownTypeCode == KnownTypeCode.String) {
							memberReferenceExpression.Target = new TypeReferenceExpression (new SimpleType ("NString"));
						} else if (p.KnownTypeCode == KnownTypeCode.Boolean) {						
							memberReferenceExpression.Target = new TypeReferenceExpression (new SimpleType ("NBoolean"));
						} else if (p.KnownTypeCode == KnownTypeCode.Char) {
							memberReferenceExpression.Target = new TypeReferenceExpression (new SimpleType ("NChar"));
						} else if (p.KnownTypeCode == KnownTypeCode.Object) {
							memberReferenceExpression.Target = new TypeReferenceExpression (new SimpleType ("NObject"));
						} else if (GetJsConstructor (p) == "Number") {
							memberReferenceExpression.Target = new TypeReferenceExpression (new SimpleType ("NNumber"));
						}
					}
					else {

						var tr = tre != null ? GetTypeRef (tre.Type) : null;

						var name = tr != null ? tr.FullName : "";

						if (name == "System.Math") {

							switch (memberReferenceExpression.MemberName) {
							case "Abs":
								memberReferenceExpression.MemberName = "abs";
								break;
							case "Sqrt":
								memberReferenceExpression.MemberName = "sqrt";
								break;
							case "Exp":
								memberReferenceExpression.MemberName = "exp";
								break;
							case "Pow":
								memberReferenceExpression.MemberName = "pow";
								break;
							case "Floor":
								memberReferenceExpression.MemberName = "floor";
								break;
							case "Ceiling":
								memberReferenceExpression.MemberName = "ceil";
								break;
							case "Cos":
								memberReferenceExpression.MemberName = "cos";
								break;
							case "Acos":
								memberReferenceExpression.MemberName = "acos";
								break;
							case "Sin":
								memberReferenceExpression.MemberName = "sin";
								break;
							case "Asin":
								memberReferenceExpression.MemberName = "asin";
								break;
							case "Atan":
								memberReferenceExpression.MemberName = "atan";
								break;
							case "Atan2":
								memberReferenceExpression.MemberName = "atan2";
								break;
							case "Tan":
								memberReferenceExpression.MemberName = "tan";
								break;
							case "Min":
								memberReferenceExpression.MemberName = "min";
								break;
							case "Max":
								memberReferenceExpression.MemberName = "max";
								break;
							default:
								memberReferenceExpression.Target = new TypeReferenceExpression (new SimpleType ("NMath"));
								break;
							}
						} else if (name == "System.Array") {
							memberReferenceExpression.Target = new TypeReferenceExpression (new SimpleType ("NArray"));
						} else if (name == "System.Console") {
							memberReferenceExpression.Target = new TypeReferenceExpression (new SimpleType ("NConsole"));
						}
					}

				} else {

				}
			}

			public override void VisitInvocationExpression (InvocationExpression invocationExpression)
			{
				base.VisitInvocationExpression (invocationExpression);

				var memberReferenceExpression = invocationExpression.Target as MemberReferenceExpression;

				if (memberReferenceExpression == null)
					return;

				var t = GetTypeRef (memberReferenceExpression.Target);
				if (t == null)
					return;

				HashSet<string> repls = null;
				string newTypeName = null;
				if (t.FullName == "System.String") {
					repls = stringRepls;
					newTypeName = "NString";
				} else if (t.FullName == "System.Boolean") {
					repls = boolRepls;
					newTypeName = "NBoolean";
				} else if (t != null && t.IsPrimitive) {
					repls = numberRepls;
					newTypeName = "NNumber";
				}
				if (repls != null && repls.Contains (memberReferenceExpression.MemberName)) {
					if (memberReferenceExpression.MemberName == "Equals") {
						var left = memberReferenceExpression.Target;
						var right = invocationExpression.Arguments.First ();
						left.Remove ();
						right.Remove ();
						invocationExpression.ReplaceWith (new BinaryOperatorExpression (left, BinaryOperatorType.Equality, right));
					} else {
						var n = new InvocationExpression (new MemberReferenceExpression (new TypeReferenceExpression (new SimpleType (newTypeName)), memberReferenceExpression.MemberName), new Expression[] {
							memberReferenceExpression.Target.Clone ()
						}.Concat (invocationExpression.Arguments.Select (x => x.Clone ())));
						var td = t.Resolve ();
						var meth = td.Methods.First (x => x.Name == memberReferenceExpression.MemberName);
						n.AddAnnotation (meth.ReturnType);
						invocationExpression.ReplaceWith (n);
					}
				}
			}
		}

		static HashSet<string> numberRepls = new HashSet<string>
		{
			"GetHashCode",
			"ToString",
		};

		static HashSet<string> boolRepls = new HashSet<string>
		{
			"GetHashCode",
			"ToString",
		};

		static HashSet<string> stringRepls = new HashSet<string>
		{
			"ToLowerInvariant",
			"ToUpperInvariant",
			"GetHashCode",
			"Replace",
			"IndexOf",
			"IndexOfAny",
			"StartsWith",
			"Substring",
			"Trim",
			"TrimStart",
			"TrimEnd",
			"Equals",
			"Remove",
		};

		class SuperPropertiesToThis : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitMemberReferenceExpression (MemberReferenceExpression memberReferenceExpression)
			{
				base.VisitMemberReferenceExpression (memberReferenceExpression);

				var s = memberReferenceExpression.Target as BaseReferenceExpression;
				if (s != null) {

					if (memberReferenceExpression.Annotation<PropertyDefinition>() != null ||
						memberReferenceExpression.Annotation<PropertyReference>() != null) {

						memberReferenceExpression.Target = new ThisReferenceExpression ();
					}

				}
			}
		}

		class FixIsOp : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitIsExpression (IsExpression isExpression)
			{
				base.VisitIsExpression (isExpression);

				var p = isExpression.Type as PrimitiveType;

				if (p != null) {

					var e = isExpression.Expression;
					e.Remove ();

					var n = new BinaryOperatorExpression (
								new MemberReferenceExpression (e, "constructor"),
						        BinaryOperatorType.Equality,
						new TypeReferenceExpression (GetJsConstructorType (isExpression.Type)));

					isExpression.ReplaceWith (n);
				}
			}
		}

		class ExpandIndexers : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitIndexerExpression (IndexerExpression indexerExpression)
			{
				base.VisitIndexerExpression (indexerExpression);

				var tr = GetTypeRef (indexerExpression.Target);

				if (tr != null && tr.IsArray)
					return;

				var td = GetTypeDef (indexerExpression.Target);

				if (td != null) {

					var m = FindMethod (td, "get_Item");
					if (m != null) {

						var t = indexerExpression.Target;

						var pa = indexerExpression.Parent as AssignmentExpression;

						if (pa != null && pa.Left == indexerExpression) {

							var s = new InvocationExpression (
								new MemberReferenceExpression (t.Clone(), "set_Item"),
								indexerExpression.Arguments.Concat (new[]{pa.Right.Clone()}).Select (x => x.Clone ()));

							pa.ReplaceWith (s);



						} else {

							var s = new InvocationExpression (
								new MemberReferenceExpression (t.Clone(), "get_Item"),
								indexerExpression.Arguments.Select (x => x.Clone ()));

							indexerExpression.ReplaceWith (s);
						}

					}

				}
			}
		}


		class ExpandOperators : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}	
			public override void VisitBinaryOperatorExpression (BinaryOperatorExpression binaryOperatorExpression)
			{
				base.VisitBinaryOperatorExpression (binaryOperatorExpression);

				var leftT = GetTypeDef (binaryOperatorExpression.Left);

				if (leftT != null && !(leftT.IsPrimitive || leftT.FullName=="System.String")) {

					var name = "";

					switch (binaryOperatorExpression.Operator) {
					case BinaryOperatorType.Add:
						name = "op_Addition";
						break;
					case BinaryOperatorType.Subtract:
						name = "op_Subtraction";
						break;
					case BinaryOperatorType.Multiply:
						name = "op_Multiply";
						break;
					case BinaryOperatorType.Divide:
						name = "op_Division";
						break;
					case BinaryOperatorType.Equality:
						name = "op_Equality";
						break;
					case BinaryOperatorType.InEquality:
						name = "op_Inequality";
						break;
					}

					var m = FindMethod (leftT, name);

					if (m != null && m.DeclaringType.FullName != "System.MulticastDelegate") {
						var left = binaryOperatorExpression.Left;
						var right = binaryOperatorExpression.Right;
						left.Remove ();
						right.Remove ();
						var n = new InvocationExpression (
						new MemberReferenceExpression (new IdentifierExpression (leftT.Name), name),
							left, right);

						n.AddAnnotation (m.ReturnType);

						binaryOperatorExpression.ReplaceWith (n);
					}
				}
			}
		}

		static MethodDefinition FindMethod (TypeDefinition type, string name)
		{
			if (string.IsNullOrEmpty (name))
				return null;
			var t = type;
			var m = t.Methods.FirstOrDefault (x => x.Name == name);
			while (m == null && t != null) {
				t = t.BaseType as TypeDefinition;
				if (t != null) {
					m = t.Methods.FirstOrDefault (x => x.Name == name);
				}
			}
			return m;
		}

		static TypeDefinition GetTypeDef (AstNode expr)
		{
			var tr = GetTypeRef (expr);
			var td = tr as TypeDefinition;
			if (td == null && tr != null)
				td = tr.Resolve ();
			return td;
		}

		static TypeReference GetTypeRef (AstNode expr)
		{
			var td = expr.Annotation<TypeDefinition> ();
			if (td != null) {
				return td;
			}

			var tr = expr.Annotation<TypeReference> ();
			if (tr != null) {
				return tr;
			}

			var ti = expr.Annotation<ICSharpCode.Decompiler.Ast.TypeInformation> ();
			if (ti != null) {
				return ti.InferredType;
			}

			var ilv = expr.Annotation<ICSharpCode.Decompiler.ILAst.ILVariable> ();
			if (ilv != null) {
				return ilv.Type;
			}

			var fr = expr.Annotation<FieldDefinition> ();
			if (fr != null) {
				return fr.FieldType;
			}

			return null;
		}

		class Renames : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitMemberReferenceExpression (MemberReferenceExpression memberReferenceExpression)
			{
				base.VisitMemberReferenceExpression (memberReferenceExpression);

				if (memberReferenceExpression.MemberName == "Length") {

					var tt = GetTargetTypeRef (memberReferenceExpression);

					if (tt != null) {
						if (tt.IsArray || tt.FullName == "System.String") {

							memberReferenceExpression.MemberName = "length";
						}
					}
				}
			}

			TypeReference GetTargetTypeRef (MemberReferenceExpression memberReferenceExpression)
			{
				var pd = memberReferenceExpression.Annotation<PropertyDefinition> ();
				if (pd != null) {
					return pd.DeclaringType;
				}

				var fd = memberReferenceExpression.Annotation<FieldDefinition> ();
				if (fd == null)
					fd = memberReferenceExpression.Annotation<FieldReference> () as FieldDefinition;
				if (fd != null) {
					return fd.DeclaringType;
				}

				return GetTypeRef (memberReferenceExpression.Target);
			}
		}

		class RemoveEnumBaseType : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitTypeDeclaration (TypeDeclaration typeDeclaration)
			{
				base.VisitTypeDeclaration (typeDeclaration);

				if (typeDeclaration.ClassType == ClassType.Enum) {
					typeDeclaration.BaseTypes.Clear ();
				}
			}
		}

		class RemoveConstraints : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitMethodDeclaration (MethodDeclaration methodDeclaration)
			{
				base.VisitMethodDeclaration (methodDeclaration);
				methodDeclaration.Constraints.Clear ();
			}

			public override void VisitTypeDeclaration (TypeDeclaration typeDeclaration)
			{
				base.VisitTypeDeclaration (typeDeclaration);
				typeDeclaration.Constraints.Clear ();
			}
		}

		class FixBadNames : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitIdentifier (Identifier identifier)
			{
				base.VisitIdentifier (identifier);
				var o = identifier.Name;
				var n = o.Replace ('<', '_').Replace ('>', '_');
				if (n != o) {
					identifier.Name = n;
				}
			}
		}

		class FlattenNamespaces : IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				var ns = compilationUnit.Children.OfType<NamespaceDeclaration> ();

				foreach (var d in ns.SelectMany (x => x.Members)) {
					d.Remove ();
					compilationUnit.AddChild (d, SyntaxTree.MemberRole);
				}

				foreach (var n in ns) {
					n.Remove ();
				}

			}
		}

		class MakePrimitiveTypesJsTypes : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public static PrimitiveType GetPrimitiveTypeReplacement (PrimitiveType primitiveType)
			{
				switch (primitiveType.KnownTypeCode) {
				case KnownTypeCode.Object:
					return new PrimitiveType ("any");
				case KnownTypeCode.Boolean:
					return new PrimitiveType ("boolean");
				case KnownTypeCode.String:
					return new PrimitiveType ("string");
				case KnownTypeCode.Char:
					return new PrimitiveType ("number");
				case KnownTypeCode.Void:
					return new PrimitiveType ("void");
				case KnownTypeCode.Byte:
				case KnownTypeCode.SByte:
				case KnownTypeCode.Int16:
				case KnownTypeCode.Int32:
				case KnownTypeCode.Int64:
				case KnownTypeCode.IntPtr:
				case KnownTypeCode.UInt16:
				case KnownTypeCode.UInt32:
				case KnownTypeCode.UInt64:
				case KnownTypeCode.Decimal:
				case KnownTypeCode.Single:
				case KnownTypeCode.Double:
					return new PrimitiveType ("number");
				}

				switch (primitiveType.Keyword) {
				case "String":
					return new PrimitiveType ("string");
				case "Boolean":
				case "boolean":
					return new PrimitiveType ("boolean");
				case "Number":
				case "number":
					return new PrimitiveType ("number");
				case "any":
				case "Object":
					return new PrimitiveType ("any");
				default:
					throw new NotSupportedException (primitiveType.Keyword);
				}
			}

			public override void VisitPrimitiveType (PrimitiveType primitiveType)
			{
				base.VisitPrimitiveType (primitiveType);

				primitiveType.ReplaceWith (GetPrimitiveTypeReplacement (primitiveType));
			}

			public override void VisitSimpleType (SimpleType simpleType)
			{
				base.VisitSimpleType (simpleType);

				if (simpleType.Identifier == "IntPtr") {
					simpleType.ReplaceWith (new PrimitiveType ("number"));
				}
			}
		}

		class AddReferences : IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				var c = new Comment ("/<reference path='mscorlib.ts'/>");
				compilationUnit.InsertChildBefore (compilationUnit.FirstChild, c, Roles.Comment);
			}

		}

		class StructToClass : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitTypeDeclaration (TypeDeclaration typeDeclaration)
			{
				base.VisitTypeDeclaration (typeDeclaration);

				if (typeDeclaration.ClassType == ClassType.Struct) {
					typeDeclaration.ClassType = ClassType.Class;
				}
			}
		}

		class IndexersToMethods : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitIndexerDeclaration (IndexerDeclaration indexerDeclaration)
			{
				base.VisitIndexerDeclaration (indexerDeclaration);

				if (!indexerDeclaration.Getter.IsNull) {
					var g = indexerDeclaration.Getter;
					var m = new MethodDeclaration {
						Name = "get_Item",
						ReturnType = indexerDeclaration.ReturnType.Clone (),
					};
					m.Parameters.AddRange (indexerDeclaration.Parameters.Select (x => (ParameterDeclaration)x.Clone ()));
					var b = g.Body;
					b.Remove ();
					m.Body = b;
					indexerDeclaration.GetParent <TypeDeclaration> ().Members.InsertBefore (indexerDeclaration, m);
				}

				if (!indexerDeclaration.Setter.IsNull) {
					var g = indexerDeclaration.Setter;
					var m = new MethodDeclaration {
						Name = "set_Item",
						ReturnType = new PrimitiveType ("void"),
					};
					m.Parameters.AddRange (indexerDeclaration.Parameters.Select (x => (ParameterDeclaration)x.Clone ()));
					m.Parameters.Add (new ParameterDeclaration {
						Name = "value",
						Type = indexerDeclaration.ReturnType.Clone (),
					});
					var b = g.Body;
					b.Remove ();
					m.Body = b;
					indexerDeclaration.GetParent <TypeDeclaration> ().Members.InsertBefore (indexerDeclaration, m);
				}

				indexerDeclaration.Remove ();
			}
		}

		class ReplaceDefault : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitDefaultValueExpression (DefaultValueExpression defaultValueExpression)
			{
				base.VisitDefaultValueExpression (defaultValueExpression);

				var ctor = GetJsConstructor (defaultValueExpression.Type);

				object val = null;

				switch (ctor) {
				case "Number":
					val = 0;
					break;
				case "Boolean":
					val = false;
					break;
				}

				defaultValueExpression.ReplaceWith (new PrimitiveExpression (val));
			}
		}

		class RemoveNullable : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitComposedType (ComposedType composedType)
			{
				base.VisitComposedType (composedType);

				if (!composedType.HasNullableSpecifier)
					return;

				var st = new SimpleType ("Nullable", composedType.BaseType.Clone ());

				composedType.ReplaceWith (st);
			}
		}

		class AnonymousInitializersNeedNames : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitAnonymousTypeCreateExpression (AnonymousTypeCreateExpression anonymousTypeCreateExpression)
			{
				base.VisitAnonymousTypeCreateExpression (anonymousTypeCreateExpression);

				foreach (var init in anonymousTypeCreateExpression.Initializers.ToList ()) {
					var ident = init as IdentifierExpression;
					if (ident != null) {
						init.ReplaceWith (new NamedExpression (ident.Identifier, ident.Clone ()));
					}
				}
			}
		}

		class FixCatches : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitTryCatchStatement (TryCatchStatement tryCatchStatement)
			{
				base.VisitTryCatchStatement (tryCatchStatement);

				var catches = tryCatchStatement.CatchClauses.ToList ();



				//
				// Fix first
				//
				foreach (var c in catches) {
					if (string.IsNullOrEmpty (c.VariableName)) {
						c.VariableName = "_ex";
					}
				}

				//
				// Merge them
				//
				if (catches.Count > 1) {
					var body = new BlockStatement ();
					var newCatch = new CatchClause {
						VariableName = "_ex",
						Body = body,
					};


					foreach (var c in catches) {

						var cbody = c.Body;
						cbody.Remove ();

						body.Add (new IfElseStatement (GetNotNullTypeCheck ("_ex", c.Type), cbody));

						c.Remove ();
					}

					tryCatchStatement.CatchClauses.Add (newCatch);
				}
			}
		}

		class FixEmptyThrow : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitThrowStatement (ThrowStatement throwStatement)
			{
				base.VisitThrowStatement (throwStatement);

				if (throwStatement.Expression.IsNull) {

					var cc = throwStatement.GetParent<CatchClause> ();

					if (cc != null) {

						throwStatement.Expression = new IdentifierExpression (cc.VariableName);

					}

				}
			}
		}

		static Expression GetNotNullTypeCheck (string id, AstType type)
		{
			return new IsExpression {
				Expression = new IdentifierExpression (id),
				Type = type.Clone (),
			};
		}

		class OperatorDeclsToMethods : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitOperatorDeclaration (OperatorDeclaration operatorDeclaration)
			{
				base.VisitOperatorDeclaration (operatorDeclaration);

				var newm = new MethodDeclaration {
					Name = GetOpName (operatorDeclaration.OperatorType),
					Modifiers = operatorDeclaration.Modifiers,
					ReturnType = operatorDeclaration.ReturnType.Clone (),
				};
				newm.Parameters.AddRange (operatorDeclaration.Parameters.Select (x => (ParameterDeclaration)x.Clone ()));
				var body = operatorDeclaration.Body;
				body.Remove ();
				newm.Body = body;
				operatorDeclaration.ReplaceWith (newm);
			}

			static string GetOpName(OperatorType op)
			{
				return "op_" + op;
			}

		}

		class InlineDelegates : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitDelegateDeclaration (DelegateDeclaration delegateDeclaration)
			{
				base.VisitDelegateDeclaration (delegateDeclaration);

				delegateDeclaration.Remove ();
			}

			public override void VisitSimpleType (SimpleType simpleType)
			{
				base.VisitSimpleType (simpleType);

				var td = GetTypeDef (simpleType);

				if (td == null || !IsDelegate (td)) {
					return;
				}

				var subs = new Dictionary<string, AstType> ();

				var invoke = td.Methods.First (x => x.Name == "Invoke");

				if (simpleType.TypeArguments.Count > 0) {

					var ps = td.GenericParameters;
					var i = 0;
					foreach (var a in simpleType.TypeArguments) {

						subs [ps [i].Name] = a;

						i++;
					}
				}

				var nt = new FunctionType ();

				foreach (var p in invoke.Parameters) {

					var pt = p.ParameterType.IsGenericParameter ? subs [p.ParameterType.Name] : GetTsType (p.ParameterType);

					nt.Parameters.Add (new ParameterDeclaration (pt.Clone (), p.Name));
				}

				nt.ReturnType = invoke.ReturnType.IsGenericParameter ? subs [invoke.ReturnType.Name] : GetTsType (invoke.ReturnType);

				if (nt.ReturnType is PrimitiveType) {
					nt.ReturnType = MakePrimitiveTypesJsTypes.GetPrimitiveTypeReplacement ((PrimitiveType)nt.ReturnType);
				}
				foreach (var p in nt.Parameters) {
					if (p.Type is PrimitiveType) {
						p.Type = MakePrimitiveTypesJsTypes.GetPrimitiveTypeReplacement ((PrimitiveType)p.Type);
					}
				}

				nt.AddAnnotation (td);

				simpleType.ReplaceWith (nt);
			}
		}

		static AstType GetTsType (TypeReference tr)
		{
			AstType r;
			switch (tr.FullName) {
			case "System.Object":
				r = new PrimitiveType ("any");
				break;
			case "System.String":
				r = new PrimitiveType ("string");
				break;
			case "System.Void":
				r = new PrimitiveType ("void");
				break;
			case "System.Boolean":
				r = new PrimitiveType ("boolean");
				break;
			case "System.Decimal":
			case "System.Single":
			case "System.Double":
			case "System.Byte":
			case "System.SByte":
			case "System.Int16":
			case "System.Int32":
			case "System.Int64":
			case "System.UInt16":
			case "System.UInt32":
			case "System.UInt64":
			case "System.IntPtr":
				r = new PrimitiveType ("number");
				break;
			default:
				if (tr.IsGenericInstance) {
					var git = (GenericInstanceType)tr;
					var st = new SimpleType (git.Name.Substring (0, git.Name.IndexOf ('`')));
					st.TypeArguments.AddRange (git.GenericArguments.Select (x => GetTsType (x)));
					r = st;
				} else {
					r = new SimpleType (tr.Name);
				}

				break;
			}
			r.AddAnnotation (tr);
			return r;
		}

		static TypeDefinition GetTypeDef (AstType type)
		{
			var td = type.Annotation<TypeDefinition> ();

			if (td == null) {
				var tr = type.Annotation<TypeReference> ();
				if (tr != null) {
					td = tr.Resolve ();
				}
			}

			return td;
		}

		static TypeReference GetTypeRef (AstType type)
		{
			return type.Annotation<TypeDefinition> () as TypeReference ?? type.Annotation<TypeReference> ();
		}

		public static bool IsDelegate(TypeDefinition typeDefinition)
		{
			if (typeDefinition == null || typeDefinition.BaseType == null)
			{
				return false;
			}
			return typeDefinition.BaseType.FullName == "System.MulticastDelegate";
		}

		class FixEvents : IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (new FixEvents1 ());
				compilationUnit.AcceptVisitor (new FixEvents2 ());
			}

			class FixEvents1 : DepthFirstAstVisitor
			{
				public override void VisitVariableDeclarationStatement (VariableDeclarationStatement variableDeclarationStatement)
				{
					base.VisitVariableDeclarationStatement (variableDeclarationStatement);

					foreach (var v in variableDeclarationStatement.Variables) {

						if (v.Initializer.IsNull || !IsEventRef (v.Initializer))
							continue;

						var wrap = new InvocationExpression (new MemberReferenceExpression (
							v.Initializer.Clone (), "ToMulticastFunction"));

						v.Initializer = wrap;

					}
				}
			}

			class FixEvents2 : DepthFirstAstVisitor
			{
				public override void VisitCustomEventDeclaration (CustomEventDeclaration eventDeclaration)
				{
					base.VisitCustomEventDeclaration (eventDeclaration);

					var fd = new FieldDeclaration {
						Name = eventDeclaration.Name,
						ReturnType = new SimpleType ("NEvent", eventDeclaration.ReturnType.Clone ()),
					};
					fd.Variables.Add (new VariableInitializer (
						eventDeclaration.Name,
						IsInterface (eventDeclaration.GetParent<TypeDeclaration> ()) ? null : new ObjectCreateExpression (new SimpleType ("NEvent", eventDeclaration.ReturnType.Clone ()))));


					eventDeclaration.ReplaceWith (fd);
				}

				public override void VisitEventDeclaration (EventDeclaration eventDeclaration)
				{
					base.VisitEventDeclaration (eventDeclaration);

					var fd = new FieldDeclaration {
						Name = eventDeclaration.Name,
						ReturnType = new SimpleType ("NEvent", eventDeclaration.ReturnType.Clone ()),
					};
					foreach (var v in eventDeclaration.Variables) {
						fd.Variables.Add (new VariableInitializer (
							v.Name,
							IsInterface (eventDeclaration.GetParent<TypeDeclaration> ()) ? null : new ObjectCreateExpression (new SimpleType ("NEvent", eventDeclaration.ReturnType.Clone ()))));

					}

					eventDeclaration.ReplaceWith (fd);
				}
			}

			static bool IsEventRef (Expression expr)
			{

				var fd = expr.Annotation<FieldDefinition> ();
				if (fd == null)
					return false;

				var t = expr.GetParent<TypeDeclaration> ();
				if (t == null)
					return false;

				var f = t.Members.FirstOrDefault (x => x.Name == fd.Name);
				if (f == null)
					return false;

				return f is CustomEventDeclaration || f is EventDeclaration;

			}
		}

		class AddAbstractMethodBodies : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}
			public override void VisitMethodDeclaration (MethodDeclaration methodDeclaration)
			{
				base.VisitMethodDeclaration (methodDeclaration);

				if (methodDeclaration.Body.IsNull && methodDeclaration.GetParent<TypeDeclaration> ().ClassType != ClassType.Interface ) {
					var block = new BlockStatement ();
					block.Add (new ThrowStatement (new ObjectCreateExpression (new SimpleType ("NotSupportedException"))));
					methodDeclaration.Body = block;

				}
			}
		}

		class RemoveAttributes : IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				foreach (var a in compilationUnit.Children.OfType<AttributeSection> ()) {
					a.Remove ();
				}
			}
		}

		class LiftNestedClasses : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitTypeDeclaration (TypeDeclaration typeDeclaration)
			{
				base.VisitTypeDeclaration (typeDeclaration);

				var children = typeDeclaration.Children.OfType<TypeDeclaration> ().ToList ();

				if (children.Count == 0)
					return;

				foreach (var n in children) {
					n.Remove ();

					if (typeDeclaration.TypeParameters.Count > 0) {
						if (n.TypeParameters.Count > 0) {
							Console.WriteLine ("WARNING Nested class is generic and so is its parent. This is not supported.");
							n.TypeParameters.AddRange (typeDeclaration.TypeParameters.Select (x => (TypeParameterDeclaration)x.Clone ()));
						}
					}

					n.Name = typeDeclaration.Name + "_" + n.Name;

					typeDeclaration.Parent.InsertChildAfter (typeDeclaration, n, NamespaceDeclaration.MemberRole);
				}

				//
				// Need to make all the privates public
				//
				foreach (var m in typeDeclaration.Members) {
					m.Modifiers &= ~(Modifiers.Private);
				}
			}

			public override void VisitMemberType (MemberType memberType)
			{
				base.VisitMemberType (memberType);


				AstNodeCollection<AstType> args = memberType.TypeArguments;

				var mems = new Stack<MemberType> ();
				var t = (AstType)memberType;
				var mem = memberType;
				while (mem != null) {
					mems.Push (mem);
					if (args.Count == 0)
						args = mem.TypeArguments;
					t = mem.Target;
					mem = t as MemberType;
				}

				var newName = "";

				var simp = t as SimpleType;
				if (simp != null) {

					if (args.Count == 0)
						args = simp.TypeArguments;
					newName += (simp.Identifier);

				} else {
					t.AcceptVisitor (this);
				}

				while (mems.Count > 0) {
					var mm = mems.Pop ();
					newName += ("_" + mm.MemberName);
				}

				var newType = new SimpleType (newName, args.Select (x => x.Clone ()));
				var td = memberType.Annotation<TypeDefinition> ();
				if (td != null) {
					newType.AddAnnotation (td);
				}
				memberType.ReplaceWith (newType);

			}
		}

		static bool IsRefParam (ParameterDeclaration x)
		{
			return (x.ParameterModifier == ParameterModifier.Ref || x.ParameterModifier == ParameterModifier.Out);
		}

		class WrapRefArgs : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitMethodDeclaration (MethodDeclaration methodDeclaration)
			{
				base.VisitMethodDeclaration (methodDeclaration);

				var hasRefs = methodDeclaration.Parameters.Any (IsRefParam);

				if (!hasRefs)
					return;

				var sub = new Substitute ();

				foreach (var p in methodDeclaration.Parameters.Where (IsRefParam).ToList ()) {

					var pty =  ((ComposedType)p.Type).BaseType;


					var access = new IndexerExpression (new IdentifierExpression (p.Name), new PrimitiveExpression (0));
					var ptd = GetTypeDef (pty);
					if (ptd != null)
						access.AddAnnotation (ptd);
					sub.Subs [p.Name] = access;
					p.ParameterModifier = ParameterModifier.None;
					var c = new ComposedType {
						BaseType = p.Type.Clone (),
					};
					c.ArraySpecifiers.Add (new ArraySpecifier (1));
					p.Type = c;
				}

				methodDeclaration.Body.AcceptVisitor (sub);
			}

			public override void VisitInvocationExpression (InvocationExpression invocationExpression)
			{
				base.VisitInvocationExpression (invocationExpression);

				var hasRefs = invocationExpression.Arguments.OfType<DirectionExpression> ().Any ();

				if (hasRefs) {

					var args = invocationExpression.Arguments.OfType<DirectionExpression> ().ToList ();

					var target = invocationExpression.Target;

					var lblock = new BlockStatement {

					};

					for (int i = 0; i < args.Count; i++) {
						var a = args [i];
						var vname = "_p" + i;
						var va = new VariableDeclarationStatement (AstType.Null, vname, new ArrayCreateExpression {
							Initializer = new ArrayInitializerExpression (a.Expression.Clone ())
						});
						a.ReplaceWith (new IdentifierExpression (vname));
						lblock.Add (va);
					}
					var rname = "_r";
					var ra = new VariableDeclarationStatement (AstType.Null, rname, invocationExpression.Clone ());
					lblock.Add (ra);
					for (int i = 0; i < args.Count; i++) {
						var a = args [i];
						var vname = "_p" + i;
						var va = new AssignmentExpression (a.Expression.Clone (), 
							new IndexerExpression (
								new IdentifierExpression (vname), new PrimitiveExpression (0)));
						lblock.Add (va);
					}
					lblock.Add (new ReturnStatement (new IdentifierExpression (rname)));

					var lambda = new LambdaExpression {
						Body = lblock,
					};

					var ilambda = new InvocationExpression (lambda);

					invocationExpression.ReplaceWith (ilambda);


//					Console.WriteLine ("SDF");
				}
			}
		}

		class DealWithStaticCtors : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitTypeDeclaration (TypeDeclaration typeDeclaration)
			{
				base.VisitTypeDeclaration (typeDeclaration);

				var ctor = typeDeclaration.Members.OfType<ConstructorDeclaration> ().FirstOrDefault (x => (x.Modifiers & Modifiers.Static) != 0);

				if (ctor != null) {

					var fctor = new FieldDeclaration {
						Modifiers = Modifiers.Private | Modifiers.Static,
						ReturnType = new PrimitiveType ("bool"),
					};
					fctor.Variables.Add (new VariableInitializer (typeDeclaration.Name + "_cctorRan", new PrimitiveExpression (false)));
					var fref = new MemberReferenceExpression (new IdentifierExpression (typeDeclaration.Name), typeDeclaration.Name + "_cctorRan");

					var b = ctor.Body;
					b.Remove ();
					var mctor = new MethodDeclaration {
						Name = typeDeclaration.Name + "_cctor",
						Modifiers = Modifiers.Static | Modifiers.Private,
						ReturnType = new PrimitiveType ("void"),
						Body = b,
					};

					var ifr = new IfElseStatement (fref, new ReturnStatement ());

					b.InsertChildBefore<Statement> (
						b.Statements.FirstOrNullObject (),
						ifr,
						BlockStatement.StatementRole);
					b.InsertChildAfter (
						ifr,
						new ExpressionStatement (
							new AssignmentExpression (fref.Clone (), new PrimitiveExpression (true))),
						BlockStatement.StatementRole);



					foreach (var m in typeDeclaration.Members.OfType <MethodDeclaration> ().Where (x => (x.Modifiers & Modifiers.Static) != 0)) {

						m.Body.InsertChildBefore<Statement> (
							m.Body.Statements.FirstOrNullObject (),
							new ExpressionStatement (new InvocationExpression (
								new MemberReferenceExpression (new IdentifierExpression (typeDeclaration.Name), mctor.Name))),
							BlockStatement.StatementRole);

					}

					foreach (var m in typeDeclaration.Members.OfType<ConstructorDeclaration> ().Where (x => (x.Modifiers & Modifiers.Static) == 0)) {
						m.Body.InsertChildBefore<Statement> (
							m.Body.Statements.FirstOrNullObject (),
							new ExpressionStatement (new InvocationExpression (
								new MemberReferenceExpression (new IdentifierExpression (typeDeclaration.Name), mctor.Name))),
							BlockStatement.StatementRole);
					}


					typeDeclaration.Members.InsertBefore (
						ctor,
						fctor);

					ctor.ReplaceWith (mctor);

				}
			}
		}

		class MergeOverloads : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitTypeDeclaration (TypeDeclaration typeDeclaration)
			{
				base.VisitTypeDeclaration (typeDeclaration);

				var mgs = typeDeclaration.Members.OfType<MethodDeclaration> ().GroupBy (
					x => x.Name + ((x.Modifiers & Modifiers.Static) != 0 ? "Static" : ""));

				var isInterface = typeDeclaration.ClassType == ClassType.Interface;

				foreach (var mg in mgs) {

					var ms = mg.ToList ();
					if (ms.Count == 1)
						continue;

					var name = ms[0].Name;

					for (int i = 0; i < ms.Count; i++) {
						var m = ms [i];
						m.Name = m.Name + "_" + i;
					}

					var newCtor = new MethodDeclaration {
						Body = new BlockStatement (),
						Name = name,
						ReturnType = ms[0].ReturnType.Clone (),
						Modifiers = ms[0].Modifiers,
					};

					var diffs = GetDiffs (ms.Select (x => x.Parameters.ToList ()).ToList ());

					newCtor.Parameters.AddRange (diffs.Item1);

					typeDeclaration.InsertChildBefore (ms [0], newCtor, Roles.TypeMemberRole);

					var isVoid = (ms [0].ReturnType is PrimitiveType) && ((PrimitiveType)ms [0].ReturnType).KnownTypeCode == KnownTypeCode.Void;
					var isStatic = ms [0].HasModifier (Modifiers.Static);

					for (int i = 0; i < diffs.Item2.Count; i++) {
						var diff = diffs.Item2 [i];
						var c = ms [i];
						var ss = ((BlockStatement)diff.TrueStatement);

						var call = new InvocationExpression (
							new MemberReferenceExpression (
								isStatic ? (Expression)new TypeReferenceExpression (new SimpleType (typeDeclaration.Name)) : new ThisReferenceExpression (),
								c.Name),
							diffs.Item1.Take (c.Parameters.Count).Select (x => new IdentifierExpression (x.Name)));

						if (isVoid) {
							ss.Add (call);
						} else {
							ss.Add (new ReturnStatement (call));
						}

						if (i + 1 < diffs.Item2.Count) {
							if (isVoid) {
								ss.Add (new ReturnStatement ());
							}
							newCtor.Body.Add (diff);
						} else {
							foreach (var x in ss.Statements) {
								x.Remove ();
								newCtor.Body.Add (x);
							}
						}

						var mo = new MethodDeclaration {
							Name = name,
							ReturnType = ms[0].ReturnType.Clone (),
							Modifiers = ms[0].Modifiers,
						};
						mo.Parameters.AddRange (ms [i].Parameters.Select (x => (ParameterDeclaration)x.Clone ()));
						foreach (var p in mo.Parameters) {
							if (!p.DefaultExpression.IsNull)
								p.DefaultExpression.Remove ();
						}
						typeDeclaration.InsertChildBefore (newCtor, mo, Roles.TypeMemberRole);
					}

					foreach (var m in ms) {
						m.Modifiers |= Modifiers.Private;
						foreach (var p in m.Parameters) {
							if (!p.DefaultExpression.IsNull)
								p.DefaultExpression.Remove ();
						}
					}


					//
					// If it's an interface, remove the bodies we created and the overloads
					//
					if (isInterface) {
						newCtor.Body.Remove ();
						foreach (var m in ms) {
							m.Remove ();
						}
					}

				}
			}
		}

		class MergeCtors : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitTypeDeclaration (TypeDeclaration typeDeclaration)
			{
				base.VisitTypeDeclaration (typeDeclaration);

				var ctors = typeDeclaration.Members.OfType<ConstructorDeclaration> ().Where (x => (x.Modifiers & Modifiers.Static)==0).ToList ();

				if (ctors.Count == 0)
					return;

				if (ctors.Count > 1) {
					for (int i = 0; i < ctors.Count; i++) {
						var c = ctors [i];
						c.Name = "constructor_" + i;
					}
				}

				//
				// Make sure everyone has a super call
				//
				foreach (var c in ctors) {
					if (c.Initializer.IsNull) {
						c.Initializer = new ConstructorInitializer {
							ConstructorInitializerType = ConstructorInitializerType.Base
						};
					}
				}

				//
				// Move this to a call
				//
				foreach (var c in ctors) {
					if (c.Initializer.ConstructorInitializerType == ConstructorInitializerType.This) {

						var thisInit = c.Initializer;
						var md = GetMethodDef (thisInit);
						var thisCtor = ctors.FirstOrDefault (x => GetMethodDef (x) == (md));

						if (thisCtor == null) {
							Console.WriteLine ("SDF");
						}

						//
						// Inline this
						//
						var sup = new InvocationExpression (new MemberReferenceExpression (new ThisReferenceExpression (), thisCtor.Name), thisInit.Arguments.Select (x => x.Clone ()));
						c.Body.InsertChildBefore (c.Body.FirstChild, new ExpressionStatement (sup), BlockStatement.StatementRole);

						//
						// Find the new super()
						//
						var subs = new Substitute ();

						ConstructorInitializer baseInit = null;
						if (baseInit == null) {



							subs.Subs = thisCtor.Parameters.Zip (thisInit.Arguments, (x, y) => Tuple.Create (x, y)).ToDictionary (x => x.Item1.Name, x => x.Item2);

							var i = thisCtor.Initializer;
							if (i.ConstructorInitializerType == ConstructorInitializerType.Base) {
								baseInit = (ConstructorInitializer)i.Clone ();
							}
						}

						if (baseInit == null) {
							throw new NotSupportedException ("This initializer to this initializer not supported");
						}

						foreach (var a in baseInit.Arguments) {
							a.AcceptVisitor (subs);
						}

						c.Initializer = baseInit;
					}
				}

				//
				//
				//
				if (ctors.Count == 1) {

					var c = ctors [0];
					var sup = GetBaseInitCall (c, new Substitute ());
					c.Body.InsertChildBefore (c.Body.FirstChild, new ExpressionStatement (sup), BlockStatement.StatementRole);
					ctors [0].Initializer.Remove ();

				} else {

					//
					// Synthesize the 1 constructor
					//
					var newCtor = new ConstructorDeclaration {
						Body = new BlockStatement (),
						Name = "constructor",
					};

					var diffs = GetDiffs (ctors.Select (x => x.Parameters.ToList ()).ToList ());

					newCtor.Parameters.AddRange (diffs.Item1);

					for (int i = 0; i < diffs.Item2.Count; i++) {
						var diff = diffs.Item2 [i];
						var c = ctors [i];
						var ss = ((BlockStatement)diff.TrueStatement);

						var subs = new Substitute ();
						var args = c.Parameters.ToList ();
						for (int j = 0; j < args.Count; j++) {
							var p = diffs.Item1 [j];
							subs.Subs [args [j].Name] = new IdentifierExpression (p.Name);
						}

						ss.Add (GetBaseInitCall (c, subs));

						ss.Add (new InvocationExpression (
							new MemberReferenceExpression (new ThisReferenceExpression (), c.Name),
							diffs.Item1.Take (c.Parameters.Count).Select (x => new IdentifierExpression (x.Name))));

						if (i + 1 < diffs.Item2.Count) {
							ss.Add (new ReturnStatement ());
							newCtor.Body.Add (diff);
						} else {
							foreach (var x in ss.Statements) {
								x.Remove ();
								newCtor.Body.Add (x);
							}
						}
					}

					typeDeclaration.InsertChildBefore (ctors [0], newCtor, Roles.TypeMemberRole);

					foreach (var c in ctors) {

						var body = c.Body;
						body.Remove ();

						//
						// Create the implementation method
						//
						var m = new MethodDeclaration {
							Name = c.Name,
							Body = body,
							ReturnType = new PrimitiveType ("void"),
							Modifiers = Modifiers.Private,
						};
						m.Parameters.AddRange (c.Parameters.Select (x => (ParameterDeclaration)x.Clone ()));
						foreach (var p in m.Parameters) {
							if (!p.DefaultExpression.IsNull)
								p.DefaultExpression.Remove ();
						}
						c.ReplaceWith (m);

						//
						// Insert overload prototype
						//
						var mo = new ConstructorDeclaration {
						};
						mo.Parameters.AddRange (c.Parameters.Select (x => (ParameterDeclaration)x.Clone ()));
						foreach (var p in mo.Parameters) {
							if (!p.DefaultExpression.IsNull)
								p.DefaultExpression.Remove ();
						}
						typeDeclaration.InsertChildBefore (newCtor, mo, Roles.TypeMemberRole);
					}
				}
			}

			Expression GetBaseInitCall (ConstructorDeclaration c, Substitute subs)
			{
				var thisInit = c.Initializer;
				var i = new InvocationExpression (
					new BaseReferenceExpression (), thisInit.Arguments.Select (x => x.Clone ()));
				i.AcceptVisitor (subs);
				return i;
			}

		}

		static MethodDefinition GetMethodDef (AstNode node)
		{
			var mr = GetMethodRef (node);

			var md = mr as MethodDefinition;
			if (md != null)
				return md;

			if (mr != null)
				return mr.Resolve ();

			return null;
		}

		static MethodReference GetMethodRef (AstNode node)
		{
			var mr = node.Annotation<MethodReference> ();
			if (mr != null)
				return mr;

			mr = node.Annotation<MethodDefinition> ();
			if (mr != null)
				return mr;

			return null;
		}

		class Substitute : DepthFirstAstVisitor
		{
			public Dictionary<string, Expression> Subs = new Dictionary<string, Expression> ();

			public override void VisitIdentifierExpression (IdentifierExpression identifierExpression)
			{
				base.VisitIdentifierExpression (identifierExpression);

				Expression change;
				if (Subs.TryGetValue (identifierExpression.Identifier, out change)) {
					identifierExpression.ReplaceWith (change.Clone ());
				}
			}
		}

		class PropertiesToMethods : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitPropertyDeclaration (PropertyDeclaration p)
			{
				if (p.Getter != null && p.Getter.Body.IsNull && p.Setter != null && p.Setter.Body.IsNull) {

					var f = new FieldDeclaration {
						Modifiers = p.Modifiers,
						ReturnType = p.ReturnType.Clone (),
					};
					f.Variables.Add (new VariableInitializer (p.Name));
					p.ReplaceWith (f);
				} else {

					foreach (var a in p.Children.OfType<Accessor> ()) {
//						a.Body.Remove ();

						var getter = a.Role == PropertyDeclaration.GetterRole;

						var fun = new MethodDeclaration {
							Body = (BlockStatement)a.Body.Clone(),
							Name = (getter ? "get " : "set ") + p.Name,
							Modifiers = p.Modifiers,
						};
						fun.AddAnnotation (a);

						if (getter) {
							fun.ReturnType = p.ReturnType.Clone ();
						}
						else {
							fun.ReturnType = new PrimitiveType ("void");
							fun.Parameters.Add (new ParameterDeclaration {
								Name = "value",
								Type = p.ReturnType.Clone (),
							});
						}

						p.Parent.InsertChildAfter (p, fun, Roles.TypeMemberRole);
					}

					p.Remove ();

				}
			}
		}

		class RemoveModifiers : DepthFirstAstVisitor, IAstTransform
		{
			public void Run (AstNode compilationUnit)
			{
				compilationUnit.AcceptVisitor (this);
			}

			public override void VisitMethodDeclaration (MethodDeclaration methodDeclaration)
			{
				base.VisitMethodDeclaration (methodDeclaration);
				methodDeclaration.Modifiers = Rem (methodDeclaration.Modifiers);
				methodDeclaration.Attributes.Clear ();
			}

			public override void VisitParameterDeclaration (ParameterDeclaration parameterDeclaration)
			{
				base.VisitParameterDeclaration (parameterDeclaration);
				parameterDeclaration.Attributes.Clear ();
			}

			public override void VisitConstructorDeclaration (ConstructorDeclaration constructorDeclaration)
			{
				base.VisitConstructorDeclaration (constructorDeclaration);
				constructorDeclaration.Modifiers = Rem (constructorDeclaration.Modifiers) & ~(Modifiers.Private);
				constructorDeclaration.Attributes.Clear ();
				constructorDeclaration.Name = "constructor";
			}

			public override void VisitPropertyDeclaration (PropertyDeclaration propertyDeclaration)
			{
				base.VisitPropertyDeclaration (propertyDeclaration);
				propertyDeclaration.Modifiers = Rem (propertyDeclaration.Modifiers);
				propertyDeclaration.Attributes.Clear ();
			}

			public override void VisitOperatorDeclaration (OperatorDeclaration operatorDeclaration)
			{
				base.VisitOperatorDeclaration (operatorDeclaration);
				operatorDeclaration.Modifiers = Rem (operatorDeclaration.Modifiers);
				operatorDeclaration.Attributes.Clear ();
			}

			public override void VisitFieldDeclaration (FieldDeclaration fieldDeclaration)
			{
				base.VisitFieldDeclaration (fieldDeclaration);
				fieldDeclaration.Modifiers = Rem (fieldDeclaration.Modifiers);
				fieldDeclaration.Attributes.Clear ();
			}

			public override void VisitVariableDeclarationStatement (VariableDeclarationStatement variableDeclarationStatement)
			{
				base.VisitVariableDeclarationStatement (variableDeclarationStatement);
				variableDeclarationStatement.Modifiers = Rem (variableDeclarationStatement.Modifiers);
			}

			public override void VisitTypeDeclaration (TypeDeclaration typeDeclaration)
			{
				base.VisitTypeDeclaration (typeDeclaration);
				typeDeclaration.Modifiers = Rem (typeDeclaration.Modifiers) & ~(Modifiers.Static | Modifiers.Private);
				typeDeclaration.Attributes.Clear ();

				if ((typeDeclaration.ClassType == ClassType.Class || typeDeclaration.ClassType == ClassType.Struct) && typeDeclaration.BaseTypes.All (IsInterface)) {
					typeDeclaration.BaseTypes.Add (new SimpleType ("NObject"));
				}
			}

			static Modifiers Rem (Modifiers m)
			{
				if ((m & Modifiers.Const) != 0) {
					m |= Modifiers.Static;
				}

				m &= ~(Modifiers.Public | Modifiers.Abstract | Modifiers.Async | Modifiers.Const | Modifiers.Protected | Modifiers.Readonly | Modifiers.Override | Modifiers.Virtual | Modifiers.Sealed | Modifiers.Internal);

				return m;
			}
		}

		public static bool IsDelegate (AstNode type)
		{
			return IsDelegate (GetTypeDef (type));
		}

		public static bool IsInterface (AstType type)
		{
			return IsInterface (GetTypeDef (type));
		}

		public static bool IsInterface (TypeDeclaration type)
		{
			return type.ClassType == ClassType.Interface;
		}

		public static bool IsInterface (TypeDefinition td)
		{
			if (td == null)
				return false;
			return td.IsInterface;
		}

		class Diff
		{
			public ParameterDeclaration[] Item1;
			public List<IfElseStatement> Item2;
		}

		static Diff GetDiffs (List<List<ParameterDeclaration>> methods)
		{
			//
			// Devise a new parameter list
			//
			var maxArgs = methods.Max (x => x.Count);
			var minArgs = methods.Min (x => x.Count);
			var ps = new ParameterDeclaration[maxArgs];

			foreach (var m in methods) {
				for (int i = 0; i < m.Count; i++) {
					var p = m [i];

					if (ps [i] == null) {
						ps [i] = (ParameterDeclaration)p.Clone ();
						if (i >= minArgs && ps[i].DefaultExpression.IsNull) {
							ps [i].AddAnnotation (new OptionalParameterNote ());
						}
					} else {

						var exName = ps [i].Name;

						if (exName != p.Name) {

							var orName = "Or" + Capitalize (p.Name);
							if (!exName.StartsWith (p.Name + "Or", StringComparison.Ordinal) &&
								!exName.Contains (orName)) {
								ps [i].Name += orName;
							}
						}

						ps [i].Type = MergeTypes (ps [i].Type, p.Type);

					}
				}
			}

			//
			// Generate the conditions
			//
			var order = methods.ToList ();

			var ifs = order.Select (x => new IfElseStatement {
				Condition = MakeCondition (x, ps),
				TrueStatement = new BlockStatement (),
			}).ToList ();

			return new Diff {
				Item1 = ps,
				Item2 = ifs
			};
		}

		static bool TypesEqual (AstType x, AstType y)
		{
			var px = x as PrimitiveType;
			var py = y as PrimitiveType;
			if (px != null && py != null && px.KnownTypeCode == py.KnownTypeCode) {
				return true;
			}

			var sx = x as SimpleType;
			var sy = y as SimpleType;
			if (sx != null && sy != null && sx.Identifier == sy.Identifier && sx.TypeArguments.Count == sy.TypeArguments.Count &&
				sx.TypeArguments.Zip (sy.TypeArguments, (a,b) => TypesEqual(a,b)).All (a => a)) {
				return true;
			}

			return false;
		}

		static AstType MergeTypes (AstType x, AstType y)
		{
			if (TypesEqual (x, y)) {
				return x.Clone ();
			}

			return new PrimitiveType ("object");
		}

		static string Capitalize (string name)
		{
			if (string.IsNullOrEmpty (name))
				return "";
			if (char.IsUpper (name [0]))
				return name;
			return char.ToUpperInvariant (name [0]) + name.Substring (1);
		}

		static Expression MakeCondition (List<ParameterDeclaration> ps, ParameterDeclaration[] newPs)
		{
			Expression e = new BinaryOperatorExpression (
				new MemberReferenceExpression (new IdentifierExpression ("arguments"), "length"),
				BinaryOperatorType.Equality,
				new PrimitiveExpression (ps.Count));

			for (int i = 0; i < ps.Count; i++) {
				var p = ps [i];

				if (IsInterface (p.Type))
					continue; // Now way to check interfaces?
				if (IsDelegate (p.Type))
					continue;

				//				var nul	lc = new UnaryOperatorExpression (UnaryOperatorType.Not, new IdentifierExpression (newPs [i].Name));
				var ctor = new IsExpression {
					Expression = new IdentifierExpression (newPs [i].Name),
					Type = GetJsConstructorType (p.Type)
				};
//				var norc = new BinaryOperatorExpression (nullc, BinaryOperatorType.ConditionalOr, ctor);
				e = new BinaryOperatorExpression (e, BinaryOperatorType.ConditionalAnd, ctor);
			}

			return e;
		}

		static AstType GetJsConstructorType (AstType type)
		{
			var tr = GetTypeDef (type);
			if (tr != null && tr.IsEnum) {
				return new PrimitiveType ("Number");
			}

			var ct = type as ComposedType;
			if (ct != null) {
				return new SimpleType ("Array");
			}

			var st = type as SimpleType;
			if (st != null) {
				var r = (SimpleType)st.Clone ();
				r.TypeArguments.Clear ();
				return r;
			}

			var pt = type as PrimitiveType;
			if (pt != null) {
				switch (pt.KnownTypeCode) {
				case KnownTypeCode.Object:
					return new PrimitiveType ("Object");
				case KnownTypeCode.String:
					return new PrimitiveType ("String");
				case KnownTypeCode.Char:
					return new PrimitiveType ("Number");
				case KnownTypeCode.Boolean:
					return new PrimitiveType ("Boolean");
				case KnownTypeCode.Byte:
				case KnownTypeCode.SByte:
				case KnownTypeCode.Int16:
				case KnownTypeCode.Int32:
				case KnownTypeCode.Int64:
				case KnownTypeCode.IntPtr:
				case KnownTypeCode.UInt16:
				case KnownTypeCode.UInt32:
				case KnownTypeCode.UInt64:
				case KnownTypeCode.Decimal:
				case KnownTypeCode.Single:
				case KnownTypeCode.Double:
					return new PrimitiveType ("Number");
				}

				switch (pt.Keyword) {
				case "any":
					return new PrimitiveType ("Object");
				case "boolean":
					return new PrimitiveType ("Boolean");
				case "number":
					return new PrimitiveType ("Number");
				}
			}

			var mt = type as MemberType;
			if (mt != null) {
				return new SimpleType (mt.MemberName);
			}

			throw new NotSupportedException ("Unknown JS constructor");
		}

		static string GetJsConstructor (AstType type)
		{
			var tr = GetTypeDef (type);
			if (tr != null && tr.IsEnum) {
				return "Number";
			}

			var ct = type as ComposedType;
			if (ct != null) {
				return "Array";
			}

			var st = type as SimpleType;
			if (st != null) {
				return st.Identifier;
			}

			var pt = type as PrimitiveType;
			if (pt != null) {
				switch (pt.KnownTypeCode) {
				case KnownTypeCode.Object:
					return "Object";
				case KnownTypeCode.String:
					return "String";
				case KnownTypeCode.Char:
					return "Number";
				case KnownTypeCode.Boolean:
					return "Boolean";
				case KnownTypeCode.Byte:
				case KnownTypeCode.SByte:
				case KnownTypeCode.Int16:
				case KnownTypeCode.Int32:
				case KnownTypeCode.Int64:
				case KnownTypeCode.IntPtr:
				case KnownTypeCode.UInt16:
				case KnownTypeCode.UInt32:
				case KnownTypeCode.UInt64:
				case KnownTypeCode.Decimal:
				case KnownTypeCode.Single:
				case KnownTypeCode.Double:
					return "Number";
				}

				switch (pt.Keyword) {
				case "any":
					return "Object";
				case "boolean":
					return "Boolean";
				case "number":
					return "Number";
				}
			}

			var mt = type as MemberType;
			if (mt != null) {
				return mt.MemberName;
			}

			throw new NotSupportedException ("Unknown JS constructor");
		}

		static int Specificity (List<ParameterDeclaration> ps)
		{
			return ps.Select (x => GetJsConstructor (x.Type)).Distinct ().Count ();
		}


	}

	class OptionalParameterNote {}
}
