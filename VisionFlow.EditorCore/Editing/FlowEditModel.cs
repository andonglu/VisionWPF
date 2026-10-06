using System;
using System.Collections.Generic;
using VisionFlow.Core;
using VisionFlow.Nodes;
using VisionFlow.Runtime;

namespace VisionFlow.Editing
{
    /// <summary>拖拽/插入时相对目标节点的位置。</summary>
    public enum FlowInsertPosition
    {
        Into,
        Above,
        Below
    }

    /// <summary>
    /// 流程编辑模型：UI 无关的流程结构编辑操作（增、删、移动、嵌套）。
    /// 窗体与无头测试都通过它修改流程，保证两种入口行为一致。
    ///
    /// 脏状态约定（VF-10）：结构编辑触发 <see cref="StructureChanged"/> 并置脏；
    /// 参数修改由 UI 层调用 <see cref="MarkDirty"/>；保存成功调 <see cref="MarkSaved"/>；
    /// 新建/打开（<see cref="ReplaceRoot"/>）重置为未脏。预览结果、日志、选择状态不算修改。
    /// </summary>
    public sealed class FlowEditModel
    {
        public SequenceNode Root { get; private set; }

        /// <summary>结构编辑（增删移动）后触发。</summary>
        public event EventHandler StructureChanged;

        /// <summary>文档是否有未保存的修改。</summary>
        public bool IsDirty { get; private set; }

        public FlowEditModel()
        {
            Root = new SequenceNode("主流程");
        }

        /// <summary>替换整个流程根（新建/打开）。替换后视为未修改。</summary>
        public void ReplaceRoot(SequenceNode root)
        {
            Root = root ?? throw new ArgumentNullException(nameof(root));
            IsDirty = false;
        }

        /// <summary>标记文档已修改（参数修改等非结构变更由 UI 层调用）。</summary>
        public void MarkDirty()
        {
            IsDirty = true;
        }

        /// <summary>保存成功后清除脏标记；保存失败不得调用。</summary>
        public void MarkSaved()
        {
            IsDirty = false;
        }

        private void OnStructureChanged()
        {
            IsDirty = true;
            StructureChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 新建一个工具箱节点并加入流程。
        /// targetContainer 为 null 时加到根；目标是容器节点时加入其对应子列表；
        /// 目标是 ToolNode 时插入到它所在列表的它之后。
        /// </summary>
        public FlowNode AddNode(string toolboxId, FlowNode targetContainer = null, IfBranch branch = IfBranch.If)
        {
            return InsertToolboxNode(toolboxId, targetContainer, branch, FlowInsertPosition.Into);
        }

        /// <summary>新建一个工具箱节点，并按指定位置插入流程。</summary>
        public FlowNode InsertToolboxNode(string toolboxId, FlowNode target = null, IfBranch branch = IfBranch.If,
            FlowInsertPosition position = FlowInsertPosition.Into)
        {
            FlowNode node = ToolboxRegistry.Find(toolboxId).Factory();
            if (!ResolveInsertLocation(target, branch, position, out IList<FlowNode> list, out int index))
            {
                throw new InvalidOperationException("无法解析节点插入位置。");
            }
            list.Insert(index, node);
            OnStructureChanged();
            return node;
        }

        /// <summary>把已有节点移动到指定目标位置。根节点不可移动，节点不能移动到自身或自身子级中。</summary>
        public bool MoveNode(FlowNode node, FlowNode target, IfBranch branch, FlowInsertPosition position)
        {
            if (!CanMoveNode(node, target, branch, position))
            {
                return false;
            }

            IList<FlowNode> sourceList = GetParentList(node);
            int oldIndex = sourceList.IndexOf(node);
            ResolveInsertLocation(target, branch, position, out IList<FlowNode> targetList, out int newIndex);

            sourceList.RemoveAt(oldIndex);
            if (sourceList == targetList && oldIndex < newIndex)
            {
                newIndex--;
            }
            targetList.Insert(newIndex, node);
            OnStructureChanged();
            return true;
        }

        public bool CanMoveNode(FlowNode node, FlowNode target, IfBranch branch, FlowInsertPosition position)
        {
            if (node == null || node == Root || node == target || GetParentList(node) == null)
            {
                return false;
            }
            if (target != null && IsDescendant(node, target))
            {
                return false;
            }
            return ResolveInsertLocation(target, branch, position, out _, out _);
        }

        /// <summary>删除节点（项目不提供撤销）。根节点不可删除；被删子树中工具的缓存资源随即释放。</summary>
        public bool RemoveNode(FlowNode node)
        {
            if (node == null || node == Root)
            {
                return false;
            }
            IList<FlowNode> parentList = GetParentList(node);
            if (parentList == null || !parentList.Remove(node))
            {
                return false;
            }
            FlowResources.Release(node);
            OnStructureChanged();
            return true;
        }

        public bool InsertExistingNode(FlowNode node, FlowNode target = null, IfBranch branch = IfBranch.If,
            FlowInsertPosition position = FlowInsertPosition.Into)
        {
            if (node == null || node == Root)
            {
                return false;
            }
            if (!ResolveInsertLocation(target, branch, position, out IList<FlowNode> list, out int index))
            {
                return false;
            }
            list.Insert(index, node);
            OnStructureChanged();
            return true;
        }

        /// <summary>在同级列表中移动节点（delta 为 -1 上移 / +1 下移）。</summary>
        public bool MoveNode(FlowNode node, int delta)
        {
            IList<FlowNode> parentList = GetParentList(node);
            if (parentList == null)
            {
                return false;
            }
            int index = parentList.IndexOf(node);
            int newIndex = index + delta;
            if (index < 0 || newIndex < 0 || newIndex >= parentList.Count)
            {
                return false;
            }
            parentList.RemoveAt(index);
            parentList.Insert(newIndex, node);
            OnStructureChanged();
            return true;
        }

        /// <summary>取容器节点的子列表；非容器返回 null。</summary>
        public static IList<FlowNode> GetChildList(FlowNode node, IfBranch branch = IfBranch.If)
        {
            if (node is SequenceNode sequence)
            {
                return sequence.Children;
            }
            if (node is IfElseNode ifElse)
            {
                return branch == IfBranch.If ? ifElse.IfBranch : ifElse.ElseBranch;
            }
            if (node is LoopNodeBase loop)
            {
                return loop.Body;
            }
            return null;
        }

        /// <summary>递归查找节点所在的父列表；根节点返回 null。</summary>
        public IList<FlowNode> GetParentList(FlowNode node)
        {
            return FindParentList(Root, node);
        }

        private bool ResolveInsertLocation(FlowNode target, IfBranch branch, FlowInsertPosition position,
            out IList<FlowNode> list, out int index)
        {
            if (target == null)
            {
                list = Root.Children;
                index = list.Count;
                return true;
            }

            if (position == FlowInsertPosition.Into)
            {
                list = GetChildList(target, branch);
                if (list != null)
                {
                    index = list.Count;
                    return true;
                }

                list = GetParentList(target);
                if (list == null)
                {
                    index = 0;
                    return false;
                }
                index = list.IndexOf(target) + 1;
                return true;
            }

            list = GetParentList(target);
            if (list == null)
            {
                index = 0;
                return false;
            }
            index = list.IndexOf(target) + (position == FlowInsertPosition.Below ? 1 : 0);
            return index >= 0;
        }

        private static bool IsDescendant(FlowNode ancestor, FlowNode node)
        {
            foreach (IfBranch branch in new[] { IfBranch.If, IfBranch.Else })
            {
                IList<FlowNode> children = GetChildList(ancestor, branch);
                if (children == null)
                {
                    break;
                }
                foreach (FlowNode child in children)
                {
                    if (child == node || IsDescendant(child, node))
                    {
                        return true;
                    }
                }
                if (!(ancestor is IfElseNode))
                {
                    break;
                }
            }
            return false;
        }

        private static IList<FlowNode> FindParentList(FlowNode current, FlowNode target)
        {
            foreach (IfBranch branch in new[] { IfBranch.If, IfBranch.Else })
            {
                IList<FlowNode> list = GetChildList(current, branch);
                if (list == null)
                {
                    break; // 非容器节点没有子列表
                }
                if (list.Contains(target))
                {
                    return list;
                }
                foreach (FlowNode child in list)
                {
                    IList<FlowNode> found = FindParentList(child, target);
                    if (found != null)
                    {
                        return found;
                    }
                }
                // IfElse 有两个分支，继续查 Else；Sequence/For 只有一份列表，查完即停
                if (!(current is IfElseNode))
                {
                    break;
                }
            }
            return null;
        }
    }
}
