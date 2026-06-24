using System.Linq;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class FieldOrderingTests
{
    [Fact]
    public void AbsoluteOrderedFields_RenderBeforeNonAbsoluteFields()
    {
        var absolute = FieldOrdering.ComputeKey("z-absolute", absoluteOrder: 1000, relativeOrder: 0, "type-b", entityTypeDisplayOrder: 10);
        var nonAbsolute = FieldOrdering.ComputeKey("a-plain", absoluteOrder: null, relativeOrder: 0, "type-a", entityTypeDisplayOrder: 10);

        Assert.True(absolute.CompareTo(nonAbsolute) < 0);
    }

    [Fact]
    public void AbsoluteFields_SortStrictlyByAbsoluteValue()
    {
        var first = FieldOrdering.ComputeKey("b", absoluteOrder: 1, relativeOrder: 0, "t", null);
        var second = FieldOrdering.ComputeKey("a", absoluteOrder: 2, relativeOrder: 0, "t", null);

        Assert.True(first.CompareTo(second) < 0);
    }

    [Fact]
    public void NonAbsoluteFields_FallBackToTypeDisplayOrder()
    {
        var earlyType = FieldOrdering.ComputeKey("z", absoluteOrder: null, relativeOrder: 0, "type-a", entityTypeDisplayOrder: 10);
        var lateType = FieldOrdering.ComputeKey("a", absoluteOrder: null, relativeOrder: 0, "type-b", entityTypeDisplayOrder: 20);

        Assert.True(earlyType.CompareTo(lateType) < 0);
    }

    [Fact]
    public void NonAbsoluteFields_OrderByRelativeWithinSameType()
    {
        var lowerRelative = FieldOrdering.ComputeKey("z", absoluteOrder: null, relativeOrder: 1, "type-a", entityTypeDisplayOrder: 10);
        var higherRelative = FieldOrdering.ComputeKey("a", absoluteOrder: null, relativeOrder: 2, "type-a", entityTypeDisplayOrder: 10);

        Assert.True(lowerRelative.CompareTo(higherRelative) < 0);
    }

    [Fact]
    public void Fields_BreakTiesByName()
    {
        var alpha = FieldOrdering.ComputeKey("alpha", absoluteOrder: null, relativeOrder: 0, "type-a", entityTypeDisplayOrder: 10);
        var beta = FieldOrdering.ComputeKey("beta", absoluteOrder: null, relativeOrder: 0, "type-a", entityTypeDisplayOrder: 10);

        Assert.True(alpha.CompareTo(beta) < 0);
    }

    [Fact]
    public void Order_ProducesExpectedSequence()
    {
        var keys = new[]
        {
            ("plain-b", FieldOrdering.ComputeKey("plain-b", null, 0, "type-a", 10)),
            ("absolute", FieldOrdering.ComputeKey("absolute", 5, 0, "type-a", 10)),
            ("plain-a", FieldOrdering.ComputeKey("plain-a", null, 0, "type-a", 10)),
        };

        var ordered = FieldOrdering.Order(keys, static item => item.Item2).Select(static item => item.Item1).ToArray();

        Assert.Equal(new[] { "absolute", "plain-a", "plain-b" }, ordered);
    }
}
