// 验证节点编辑缓冲提交后会同步宏值和脏状态。
using FluentAssertions;
using WinUIMacro.Contracts;
using WinUIMacro.UI.ViewModels;

namespace WinUIMacro.Tests.ViewModels;

[TestClass]
public sealed class MacroEditorViewModelTests
{
    [TestMethod]
    public void CommitActiveEdits_ChangedNote_CommitsValueAndMarksMacroDirty()
    {
        var macro = CreatePersistedMacro();
        var node = macro.Nodes.Single();
        node.BeginEdit();
        node.EditableValue = "新备注";

        macro.CommitActiveEdits();

        node.Value.Should().Be("新备注");
        node.IsEditing.Should().BeFalse();
        macro.IsDirty.Should().BeTrue();
    }

    [TestMethod]
    public void CommitActiveEdits_UnchangedNote_LeavesMacroClean()
    {
        var macro = CreatePersistedMacro();
        var node = macro.Nodes.Single();
        node.BeginEdit();

        macro.CommitActiveEdits();

        node.Value.Should().Be("原备注");
        node.IsEditing.Should().BeFalse();
        macro.IsDirty.Should().BeFalse();
    }

    [TestMethod]
    public void CopyAndPasteNodes_SortsSelectionAndCreatesIndependentNodes()
    {
        var macro = CreatePersistedMacro("A", "B", "C", "D", "E");
        var originalB = macro.Nodes[1];
        var originalD = macro.Nodes[3];

        macro.CopyNodes([originalD, originalB]);
        originalB.Value = "已修改";
        var pastedNodes = macro.PasteNodesAfterSelection([originalB, originalD]);

        pastedNodes.Select(node => node.Value).Should().Equal("B", "D");
        macro
            .Nodes.Select(node => node.Value)
            .Should()
            .Equal("A", "已修改", "C", "D", "B", "D", "E");
        pastedNodes
            .Should()
            .OnlyContain(node =>
                !ReferenceEquals(node, originalB) && !ReferenceEquals(node, originalD)
            );
    }

    [TestMethod]
    public void PasteNodesWithoutSelection_AppendsCopiedNodes()
    {
        var macro = CreatePersistedMacro("A", "B", "C");
        macro.CopyNodes([macro.Nodes[1]]);

        var pastedNodes = macro.PasteNodesAfterSelection([]);

        pastedNodes.Should().ContainSingle().Which.Value.Should().Be("B");
        macro.Nodes.Select(node => node.Value).Should().Equal("A", "B", "C", "B");
    }

    [TestMethod]
    public void DeleteNodes_RemovesDescendingAndReturnsSmallestDeletedIndex()
    {
        var macro = CreatePersistedMacro("A", "B", "C", "D", "E");

        var firstRemovedIndex = macro.DeleteNodes([macro.Nodes[3], macro.Nodes[1]]);

        firstRemovedIndex.Should().Be(1);
        macro.Nodes.Select(node => node.Value).Should().Equal("A", "C", "E");
    }

    [TestMethod]
    public void MoveNodes_PreservesRelativeOrderAndCreatesContiguousGroup()
    {
        var macro = CreatePersistedMacro("A", "B", "C", "D", "E", "F");
        var movedNodes = new[] { macro.Nodes[3], macro.Nodes[1] };

        macro.MoveNodes(movedNodes, macro.Nodes.Count);

        macro.Nodes.Select(node => node.Value).Should().Equal("A", "C", "E", "F", "B", "D");
        macro.Nodes.Skip(4).Should().Equal(movedNodes.Reverse());
    }

    private static MacroEditorViewModel CreatePersistedMacro() => CreatePersistedMacro("原备注");

    private static MacroEditorViewModel CreatePersistedMacro(params string[] values) =>
        new(
            new MacroDefinition(
                Guid.NewGuid(),
                "测试宏",
                MacroPlaybackMode.Once,
                [.. values.Select(value => new MacroNode(MacroNodeType.Note, value))]
            ),
            isPersisted: true
        );
}
