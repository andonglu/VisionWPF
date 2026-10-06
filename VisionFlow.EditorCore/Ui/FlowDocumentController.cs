using System;
using System.Collections.Generic;
using System.IO;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Runtime;
using VisionFlow.Validation;

namespace VisionFlow.Ui
{
    /// <summary>关闭未保存文档时的用户选择。</summary>
    public enum ConfirmCloseChoice
    {
        /// <summary>先保存再关闭；保存失败则中止关闭。</summary>
        Save,
        /// <summary>放弃修改直接关闭。</summary>
        Discard,
        /// <summary>取消关闭。</summary>
        Cancel
    }

    /// <summary>加载流程的结果：兼容性警告与加载后校验。</summary>
    public sealed class FlowLoadResult
    {
        public IReadOnlyList<string> Warnings { get; set; } = new List<string>();
        public FlowValidationResult Validation { get; set; }
    }

    /// <summary>
    /// 流程文档控制器（VF-08 从 MainWindow 平移）：持有当前文档路径，
    /// 封装 新建/加载/保存/另存为/关闭确认 的编排逻辑。
    /// 不依赖具体 UI 框架：保存对话框、关闭确认、警告提示均通过构造注入的委托完成。
    /// </summary>
    public sealed class FlowDocumentController
    {
        private readonly FlowEditModel _model;
        /// <summary>另存对话框：入参为当前路径（可为空），返回用户选择的文件路径，取消返回 null。</summary>
        private readonly Func<string, string> _showSaveDialog;
        /// <summary>关闭确认：仅在文档有未保存修改时调用。</summary>
        private readonly Func<ConfirmCloseChoice> _confirmClose;
        /// <summary>警告提示（标题, 内容）。</summary>
        private readonly Action<string, string> _showWarning;

        public FlowDocumentController(
            FlowEditModel model,
            Func<string, string> showSaveDialog,
            Func<ConfirmCloseChoice> confirmClose,
            Action<string, string> showWarning)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _showSaveDialog = showSaveDialog ?? throw new ArgumentNullException(nameof(showSaveDialog));
            _confirmClose = confirmClose ?? throw new ArgumentNullException(nameof(confirmClose));
            _showWarning = showWarning ?? throw new ArgumentNullException(nameof(showWarning));
        }

        /// <summary>当前流程文件路径；未保存过的新文档为 null。</summary>
        public string CurrentFlowPath { get; private set; }

        /// <summary>文档保存成功（含另存为）后触发，参数为实际保存路径。</summary>
        public event Action<string> Saved;

        /// <summary>新建空白流程文档。旧流程的运行期缓存资源随即释放。</summary>
        public void New()
        {
            SequenceNode previous = _model.Root;
            _model.ReplaceRoot(new SequenceNode("主流程"));
            CurrentFlowPath = null;
            FlowResources.Release(previous);
        }

        /// <summary>
        /// 从文件加载流程并替换当前文档，返回兼容性警告与校验结果。
        /// 旧流程的缓存资源在替换成功后释放；新流程不在此预热，由调用方决定同步或后台调用 <see cref="FlowResources.Prepare"/>。
        /// </summary>
        public FlowLoadResult Load(string fileName)
        {
            var loadWarnings = new List<string>();
            SequenceNode root = FlowSerializer.LoadFile(fileName, loadWarnings);
            SequenceNode previous = _model.Root;
            _model.ReplaceRoot(root);
            CurrentFlowPath = Path.GetFullPath(fileName);
            FlowResources.Release(previous);
            return new FlowLoadResult
            {
                Warnings = loadWarnings,
                Validation = FlowValidator.Validate(_model.Root)
            };
        }

        /// <summary>
        /// 保存当前流程。saveAs 或无路径时先询问保存位置；
        /// 写盘失败时保护原文件与文档状态（FlowFileSaveService）。
        /// </summary>
        public bool Save(bool saveAs)
        {
            try
            {
                string fileName = CurrentFlowPath;
                if (saveAs || string.IsNullOrWhiteSpace(fileName))
                {
                    fileName = _showSaveDialog(fileName);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        return false;
                    }
                }

                fileName = Path.GetFullPath(NormalizeFlowFileName(fileName));
                if (!SaveToFile(fileName))
                {
                    return false;
                }
                _model.MarkSaved();
                CurrentFlowPath = fileName;
                // 子流程的相对路径相对于流程文件所在目录
                FlowSerializer.SetSubFlowBaseDirectory(_model.Root, Path.GetDirectoryName(fileName));
                Saved?.Invoke(fileName);
                return true;
            }
            catch (Exception ex) when (FlowFileSaveService.IsSaveException(ex))
            {
                _showWarning("保存失败", "流程未保存，请检查文件路径和写入权限。\r\n" + ex.GetBaseException().Message);
                return false;
            }
        }

        /// <summary>
        /// 关闭当前文档前的确认（VF-10）：无未保存修改直接放行；
        /// 有修改时询问 保存/放弃/取消，保存失败或取消都中止关闭。
        /// </summary>
        public bool ConfirmClose()
        {
            if (!_model.IsDirty)
            {
                return true;
            }
            switch (_confirmClose())
            {
                case ConfirmCloseChoice.Save:
                    return Save(false);
                case ConfirmCloseChoice.Discard:
                    return true;
                default:
                    return false;
            }
        }

        private bool SaveToFile(string fileName)
        {
            if (FlowFileSaveService.TrySave(fileName, () => FlowSerializer.Save(_model.Root), out string error))
            {
                return true;
            }
            _showWarning("保存失败", "流程未保存，原文件和当前文档保持不变。\r\n" + error);
            return false;
        }

        /// <summary>纠正重复/缺失的 .vflow.json 扩展名。</summary>
        public static string NormalizeFlowFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
            if (fileName.EndsWith(".vflow.vflow.json", StringComparison.OrdinalIgnoreCase))
            {
                return fileName.Substring(0, fileName.Length - ".vflow.vflow.json".Length) + ".vflow.json";
            }
            if (fileName.EndsWith(".vflow", StringComparison.OrdinalIgnoreCase))
            {
                return fileName + ".json";
            }
            return fileName;
        }
    }
}
