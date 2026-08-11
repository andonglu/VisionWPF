using System;

namespace VisionFlow.Runtime
{
    /// <summary>一个可保存/加载的视觉流程配方。</summary>
    public sealed class VisionFlowRecipe
    {
        public string RecipeName { get; set; }
        public string FlowName { get; set; }
        public string Json { get; set; }
        public DateTime SavedAt { get; set; }

        public VisionFlowRecipe()
        {
            SavedAt = DateTime.Now;
        }
    }
}
