﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.PooledObjects;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp
{
    internal partial class Binder
    {
        /// <summary>
        /// If the left and right are tuples of matching cardinality, we'll try to bind the operator element-wise.
        /// When that succeeds, the element-wise conversions are collected. We keep them for semantic model.
        /// The element-wise binary operators are collected and stored as a tree for lowering.
        /// </summary>
        private BoundTupleBinaryOperator BindTupleBinaryOperator(BinaryExpressionSyntax node, BinaryOperatorKind kind,
            BoundExpression left, BoundExpression right, BindingDiagnosticBag diagnostics)
        {
            TupleBinaryOperatorInfo.Multiple operators = BindTupleBinaryOperatorNestedInfo(node, kind, left, right, diagnostics);

            BoundExpression convertedLeft = ApplyConvertedTypes(left, operators, isRight: false, diagnostics);
            BoundExpression convertedRight = ApplyConvertedTypes(right, operators, isRight: true, diagnostics);

            TypeSymbol resultType = GetSpecialType(SpecialType.System_Boolean, diagnostics, node);

            return new BoundTupleBinaryOperator(node, convertedLeft, convertedRight, kind, operators, resultType);
        }

        private BoundExpression ApplyConvertedTypes(BoundExpression expr, TupleBinaryOperatorInfo @operator, bool isRight, BindingDiagnosticBag diagnostics)
        {
            TypeSymbol convertedType = isRight ? @operator.RightConvertedTypeOpt : @operator.LeftConvertedTypeOpt;

            if (convertedType is null)
            {
                // Note: issues with default will already have been reported by BindSimpleBinaryOperator (ie. we couldn't find a suitable element-wise operator)
                if (@operator.InfoKind == TupleBinaryOperatorInfoKind.Multiple && expr is BoundTupleLiteral tuple)
                {
                    // Although the tuple will remain typeless, we'll give elements converted types as possible
                    var multiple = (TupleBinaryOperatorInfo.Multiple)@operator;
                    if (multiple.Operators.Length == 0)
                    {
                        return BindToNaturalType(expr, diagnostics, reportNoTargetType: false);
                    }

                    ImmutableArray<BoundExpression> arguments = tuple.Arguments;
                    int length = arguments.Length;
                    Debug.Assert(length == multiple.Operators.Length);

                    var builder = ArrayBuilder<BoundExpression>.GetInstance(length);
                    for (int i = 0; i < length; i++)
                    {
                        builder.Add(ApplyConvertedTypes(arguments[i], multiple.Operators[i], isRight, diagnostics));
                    }

                    return new BoundConvertedTupleLiteral(
                        tuple.Syntax, tuple, wasTargetTyped: false, builder.ToImmutableAndFree(), tuple.ArgumentNamesOpt, tuple.InferredNamesOpt, tuple.Type, tuple.HasErrors);
                }

                // This element isn't getting a converted type
                return BindToNaturalType(expr, diagnostics, reportNoTargetType: false);
            }

            // We were able to determine a converted type (for this tuple literal or element), we can just convert to it
            return GenerateConversionForAssignment(convertedType, expr, diagnostics);
        }

        /// <summary>
        /// Binds:
        /// 1. dynamically, if either side is dynamic
        /// 2. as tuple binary operator, if both sides are tuples of matching cardinalities
        /// 3. as regular binary operator otherwise
        /// </summary>
        private TupleBinaryOperatorInfo BindTupleBinaryOperatorInfo(BinaryExpressionSyntax node, BinaryOperatorKind kind,
            BoundExpression left, BoundExpression right, BindingDiagnosticBag diagnostics)
        {
            TypeSymbol leftType = left.Type;
            TypeSymbol rightType = right.Type;

            if ((object)leftType != null && leftType.IsDynamic() || (object)rightType != null && rightType.IsDynamic())
            {
                return BindTupleDynamicBinaryOperatorSingleInfo(node, kind, left, right, diagnostics);
            }

            if (IsTupleBinaryOperation(left, right))
            {
                return BindTupleBinaryOperatorNestedInfo(node, kind, left, right, diagnostics);
            }

            // https://github.com/dotnet/roslyn/issues/76130: Add test coverage for this code path

            BoundExpression comparison = BindSimpleBinaryOperator(node, diagnostics, left, right, leaveUnconvertedIfInterpolatedString: false);
            switch (comparison)
            {
                case BoundLiteral _:
                    // this case handles `null == null` and the like
                    return new TupleBinaryOperatorInfo.NullNull(kind);

                case BoundBinaryOperator binary:
                    PrepareBoolConversionAndTruthOperator(binary.Type, node, kind, diagnostics,
                        out BoundExpression conversionIntoBoolOperator, out BoundValuePlaceholder conversionIntoBoolOperatorPlaceholder,
                        out UnaryOperatorSignature boolOperator);
                    CheckConstraintLanguageVersionAndRuntimeSupportForOperator(node, boolOperator.Method, isUnsignedRightShift: false, boolOperator.ConstrainedToTypeOpt, diagnostics);

                    return new TupleBinaryOperatorInfo.Single(binary.Left.Type, binary.Right.Type, binary.OperatorKind, binary.Method, binary.ConstrainedToType,
                        conversionIntoBoolOperatorPlaceholder, conversionIntoBoolOperator, boolOperator);

                default:
                    throw ExceptionUtilities.UnexpectedValue(comparison);
            }
        }

        /// <summary>
        /// If an element-wise binary operator returns a non-bool type, we will either:
        /// - prepare a conversion to bool if one exists
        /// - prepare a truth operator: op_false in the case of an equality (<c>a == b</c> will be lowered to <c>!((a == b).op_false)</c>) or op_true in the case of inequality,
        ///     with the conversion being used for its input.
        /// </summary>
        private void PrepareBoolConversionAndTruthOperator(TypeSymbol type, BinaryExpressionSyntax node, BinaryOperatorKind binaryOperator, BindingDiagnosticBag diagnostics,
            out BoundExpression conversionForBool, out BoundValuePlaceholder conversionForBoolPlaceholder, out UnaryOperatorSignature boolOperator)
        {
            // Is the operand implicitly convertible to bool?

            CompoundUseSiteInfo<AssemblySymbol> useSiteInfo = GetNewCompoundUseSiteInfo(diagnostics);
            TypeSymbol boolean = GetSpecialType(SpecialType.System_Boolean, diagnostics, node);
            Conversion conversion = this.Conversions.ClassifyImplicitConversionFromType(type, boolean, ref useSiteInfo);
            diagnostics.Add(node, useSiteInfo);

            if (conversion.IsImplicit)
            {
                conversionForBoolPlaceholder = new BoundValuePlaceholder(node, type).MakeCompilerGenerated();
                conversionForBool = CreateConversion(node, conversionForBoolPlaceholder, conversion, isCast: false, conversionGroupOpt: null, boolean, diagnostics);
                boolOperator = default;
                return;
            }

            // It was not. Does it implement operator true (or false)?

            UnaryOperatorKind boolOpKind;
            switch (binaryOperator)
            {
                case BinaryOperatorKind.Equal:
                    boolOpKind = UnaryOperatorKind.False;
                    break;
                case BinaryOperatorKind.NotEqual:
                    boolOpKind = UnaryOperatorKind.True;
                    break;
                default:
                    throw ExceptionUtilities.UnexpectedValue(binaryOperator);
            }

            LookupResultKind resultKind;
            ImmutableArray<MethodSymbol> originalUserDefinedOperators;
            BoundExpression comparisonResult = new BoundTupleOperandPlaceholder(node, type);
            UnaryOperatorAnalysisResult best = this.UnaryOperatorOverloadResolution(boolOpKind, comparisonResult, node, diagnostics, out resultKind, out originalUserDefinedOperators);
            if (best.HasValue)
            {
                conversionForBoolPlaceholder = new BoundValuePlaceholder(node, type).MakeCompilerGenerated();
                conversionForBool = CreateConversion(node, conversionForBoolPlaceholder, best.Conversion, isCast: false, conversionGroupOpt: null, best.Signature.OperandType, diagnostics);
                boolOperator = best.Signature;
                return;
            }

            // It did not. Give a "not convertible to bool" error.

            GenerateImplicitConversionError(diagnostics, node, conversion, comparisonResult, boolean);
            conversionForBoolPlaceholder = null;
            conversionForBool = null;
            boolOperator = default;
            return;
        }

        private TupleBinaryOperatorInfo BindTupleDynamicBinaryOperatorSingleInfo(BinaryExpressionSyntax node, BinaryOperatorKind kind,
            BoundExpression left, BoundExpression right, BindingDiagnosticBag diagnostics)
        {
            // This method binds binary == and != operators where one or both of the operands are dynamic.
            Debug.Assert((object)left.Type != null && left.Type.IsDynamic() || (object)right.Type != null && right.Type.IsDynamic());

            bool hasError = false;
            if (!IsLegalDynamicOperand(left) || !IsLegalDynamicOperand(right))
            {
                // Operator '{0}' cannot be applied to operands of type '{1}' and '{2}'
                Error(diagnostics, ErrorCode.ERR_BadBinaryOps, node, node.OperatorToken.Text, left.Display, right.Display);
                hasError = true;
            }

            BinaryOperatorKind elementOperatorKind = hasError ? kind : kind.WithType(BinaryOperatorKind.Dynamic);
            TypeSymbol dynamicType = hasError ? CreateErrorType() : Compilation.DynamicType;

            // We'll want to dynamically invoke operators op_true (/op_false) for equality (/inequality) comparison, but we don't need
            // to prepare either a conversion or a truth operator. Those can just be synthesized during lowering.
            return new TupleBinaryOperatorInfo.Single(dynamicType, dynamicType, elementOperatorKind,
                methodSymbolOpt: null, constrainedToTypeOpt: null, conversionForBoolPlaceholder: null, conversionForBool: null, boolOperator: default);
        }

        private TupleBinaryOperatorInfo.Multiple BindTupleBinaryOperatorNestedInfo(BinaryExpressionSyntax node, BinaryOperatorKind kind,
            BoundExpression left, BoundExpression right, BindingDiagnosticBag diagnostics)
        {
            left = GiveTupleTypeToDefaultLiteralIfNeeded(left, right.Type);
            right = GiveTupleTypeToDefaultLiteralIfNeeded(right, left.Type);

            if (left.IsLiteralDefaultOrImplicitObjectCreation() ||
                right.IsLiteralDefaultOrImplicitObjectCreation())
            {
                ReportBinaryOperatorError(node, diagnostics, node.OperatorToken, left, right, LookupResultKind.Ambiguous);
                return TupleBinaryOperatorInfo.Multiple.ErrorInstance;
            }

            // Aside from default (which we fixed or ruled out above) and tuple literals,
            // we must have typed expressions at this point
            Debug.Assert((object)left.Type != null || left.Kind == BoundKind.TupleLiteral);
            Debug.Assert((object)right.Type != null || right.Kind == BoundKind.TupleLiteral);

            int leftCardinality = GetTupleCardinality(left);
            int rightCardinality = GetTupleCardinality(right);

            if (leftCardinality != rightCardinality)
            {
                Error(diagnostics, ErrorCode.ERR_TupleSizesMismatchForBinOps, node, leftCardinality, rightCardinality);
                return TupleBinaryOperatorInfo.Multiple.ErrorInstance;
            }

            (ImmutableArray<BoundExpression> leftParts, ImmutableArray<string> leftNames) = GetTupleArgumentsOrPlaceholders(left);
            (ImmutableArray<BoundExpression> rightParts, ImmutableArray<string> rightNames) = GetTupleArgumentsOrPlaceholders(right);
            ReportNamesMismatchesIfAny(left, right, leftNames, rightNames, diagnostics);

            int length = leftParts.Length;
            Debug.Assert(length == rightParts.Length);

            var operatorsBuilder = ArrayBuilder<TupleBinaryOperatorInfo>.GetInstance(length);

            for (int i = 0; i < length; i++)
            {
                operatorsBuilder.Add(BindTupleBinaryOperatorInfo(node, kind, leftParts[i], rightParts[i], diagnostics));
            }

            var compilation = this.Compilation;
            var operators = operatorsBuilder.ToImmutableAndFree();

            // typeless tuple literals are not nullable
            bool leftNullable = left.Type?.IsNullableType() == true;
            bool rightNullable = right.Type?.IsNullableType() == true;
            bool isNullable = leftNullable || rightNullable;

            TypeSymbol leftTupleType = MakeConvertedType(operators.SelectAsArray(o => o.LeftConvertedTypeOpt), node.Left, leftParts, leftNames, isNullable, compilation, diagnostics);
            TypeSymbol rightTupleType = MakeConvertedType(operators.SelectAsArray(o => o.RightConvertedTypeOpt), node.Right, rightParts, rightNames, isNullable, compilation, diagnostics);

            return new TupleBinaryOperatorInfo.Multiple(operators, leftTupleType, rightTupleType);
        }

        /// <summary>
        /// If an element in a tuple literal has an explicit name which doesn't match the name on the other side, we'll warn.
        /// The user can either remove the name, or fix it.
        ///
        /// This method handles two expressions, each of which is either a tuple literal or an expression with tuple type.
        /// In a tuple literal, each element can have an explicit name, an inferred name or no name.
        /// In an expression of tuple type, each element can have a name or not.
        /// </summary>
        private static void ReportNamesMismatchesIfAny(BoundExpression left, BoundExpression right,
            ImmutableArray<string> leftNames, ImmutableArray<string> rightNames, BindingDiagnosticBag diagnostics)
        {
            bool leftIsTupleLiteral = left is BoundTupleExpression;
            bool rightIsTupleLiteral = right is BoundTupleExpression;

            if (!leftIsTupleLiteral && !rightIsTupleLiteral)
            {
                return;
            }

            bool leftNoNames = leftNames.IsDefault;
            bool rightNoNames = rightNames.IsDefault;

            if (leftNoNames && rightNoNames)
            {
                return;
            }

            Debug.Assert(leftNoNames || rightNoNames || leftNames.Length == rightNames.Length);

            ImmutableArray<bool> leftInferred = leftIsTupleLiteral ? ((BoundTupleExpression)left).InferredNamesOpt : default;
            bool leftNoInferredNames = leftInferred.IsDefault;

            ImmutableArray<bool> rightInferred = rightIsTupleLiteral ? ((BoundTupleExpression)right).InferredNamesOpt : default;
            bool rightNoInferredNames = rightInferred.IsDefault;

            int length = leftNoNames ? rightNames.Length : leftNames.Length;
            for (int i = 0; i < length; i++)
            {
                string leftName = leftNoNames ? null : leftNames[i];
                string rightName = rightNoNames ? null : rightNames[i];

                bool different = string.CompareOrdinal(rightName, leftName) != 0;
                if (!different)
                {
                    continue;
                }

                bool leftWasInferred = leftNoInferredNames ? false : leftInferred[i];
                bool rightWasInferred = rightNoInferredNames ? false : rightInferred[i];

                bool leftComplaint = leftIsTupleLiteral && leftName != null && !leftWasInferred;
                bool rightComplaint = rightIsTupleLiteral && rightName != null && !rightWasInferred;

                if (!leftComplaint && !rightComplaint)
                {
                    // No complaints, let's move on
                    continue;
                }

                // When in doubt, we'll complain on the right side if it's a literal
                bool useRight = (leftComplaint && rightComplaint) ? rightIsTupleLiteral : rightComplaint;
                Location location = ((BoundTupleExpression)(useRight ? right : left)).Arguments[i].Syntax.Parent.Location;
                string complaintName = useRight ? rightName : leftName;

                diagnostics.Add(ErrorCode.WRN_TupleBinopLiteralNameMismatch, location, complaintName);
            }
        }

        internal static BoundExpression GiveTupleTypeToDefaultLiteralIfNeeded(BoundExpression expr, TypeSymbol targetType)
        {
            if (!expr.IsLiteralDefault() || targetType is null)
            {
                return expr;
            }

            Debug.Assert(targetType.StrippedType().IsTupleType);
            return new BoundDefaultExpression(expr.Syntax, targetType);
        }

        private static bool IsTupleBinaryOperation(BoundExpression left, BoundExpression right)
        {
            bool leftDefaultOrNew = left.IsLiteralDefaultOrImplicitObjectCreation();
            bool rightDefaultOrNew = right.IsLiteralDefaultOrImplicitObjectCreation();
            if (leftDefaultOrNew && rightDefaultOrNew)
            {
                return false;
            }

            return (GetTupleCardinality(left) > 1 || leftDefaultOrNew) &&
                   (GetTupleCardinality(right) > 1 || rightDefaultOrNew);
        }

        private static int GetTupleCardinality(BoundExpression expr)
        {
            if (expr is BoundTupleExpression tuple)
            {
                return tuple.Arguments.Length;
            }

            TypeSymbol type = expr.Type;
            if (type is null)
            {
                return -1;
            }

            if (type.StrippedType() is { IsTupleType: true } tupleType)
            {
                return tupleType.TupleElementTypesWithAnnotations.Length;
            }

            return -1;
        }

        /// <summary>
        /// Given a tuple literal or expression, we'll get two arrays:
        /// - the elements from the literal, or some placeholder with proper type (for tuple expressions)
        /// - the elements' names
        /// </summary>
        private static (ImmutableArray<BoundExpression> Elements, ImmutableArray<string> Names) GetTupleArgumentsOrPlaceholders(BoundExpression expr)
        {
            if (expr is BoundTupleExpression tuple)
            {
                return (tuple.Arguments, tuple.ArgumentNamesOpt);
            }

            // placeholder bound nodes with the proper types are sufficient to bind the element-wise binary operators
            TypeSymbol tupleType = expr.Type.StrippedType();
            ImmutableArray<BoundExpression> placeholders = tupleType.TupleElementTypesWithAnnotations
                .SelectAsArray((t, s) => (BoundExpression)new BoundTupleOperandPlaceholder(s, t.Type), expr.Syntax);

            return (placeholders, tupleType.TupleElementNames);
        }

        /// <summary>
        /// Make a tuple type (with appropriate nesting) from the types (on the left or on the right) collected
        /// from binding element-wise binary operators.
        /// If any of the elements is typeless, then the tuple is typeless too.
        /// </summary>
        private TypeSymbol MakeConvertedType(ImmutableArray<TypeSymbol> convertedTypes, CSharpSyntaxNode syntax,
            ImmutableArray<BoundExpression> elements, ImmutableArray<string> names,
            bool isNullable, CSharpCompilation compilation, BindingDiagnosticBag diagnostics)
        {
            foreach (var convertedType in convertedTypes)
            {
                if (convertedType is null)
                {
                    return null;
                }
            }

            ImmutableArray<Location> elementLocations = elements.SelectAsArray(e => e.Syntax.Location);

            var tuple = NamedTypeSymbol.CreateTuple(locationOpt: null,
                elementTypesWithAnnotations: convertedTypes.SelectAsArray(t => TypeWithAnnotations.Create(t)),
                elementLocations, elementNames: names, compilation,
                shouldCheckConstraints: true, includeNullability: false, errorPositions: default, syntax, diagnostics);

            if (!isNullable)
            {
                return tuple;
            }

            // Any violated constraints on nullable tuples would have been reported already
            NamedTypeSymbol nullableT = GetSpecialType(SpecialType.System_Nullable_T, diagnostics, syntax);
            return nullableT.Construct(tuple);
        }
    }
}
