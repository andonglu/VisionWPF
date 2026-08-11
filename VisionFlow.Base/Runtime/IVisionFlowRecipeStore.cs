using System.Collections.Generic;

namespace VisionFlow.Runtime
{
    /// <summary>配方存储抽象。设备项目可用文件、数据库或现有产品目录实现。</summary>
    public interface IVisionFlowRecipeStore
    {
        VisionFlowRecipe Load(string recipeName, string flowName);
        void Save(string recipeName, string flowName, VisionFlowRecipe recipe);
        IEnumerable<string> ListRecipes();
        IEnumerable<string> ListFlows(string recipeName);
    }
}
