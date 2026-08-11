using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VisionFlow.Runtime
{
    /// <summary>
    /// 文件型配方存储。目录结构：
    /// root\RecipeName\FlowName.vflow.json
    /// </summary>
    public sealed class FileVisionFlowRecipeStore : IVisionFlowRecipeStore
    {
        private readonly string _rootDirectory;

        public FileVisionFlowRecipeStore(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                throw new ArgumentException("配方根目录不能为空", nameof(rootDirectory));
            }
            _rootDirectory = rootDirectory;
        }

        public VisionFlowRecipe Load(string recipeName, string flowName)
        {
            string path = PathOf(recipeName, flowName);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"视觉流程配方不存在：{recipeName}/{flowName}", path);
            }
            return new VisionFlowRecipe
            {
                RecipeName = recipeName,
                FlowName = flowName,
                Json = File.ReadAllText(path),
                SavedAt = File.GetLastWriteTime(path)
            };
        }

        public void Save(string recipeName, string flowName, VisionFlowRecipe recipe)
        {
            if (recipe == null)
            {
                throw new ArgumentNullException(nameof(recipe));
            }
            if (string.IsNullOrWhiteSpace(recipe.Json))
            {
                throw new ArgumentException("配方 JSON 不能为空", nameof(recipe));
            }

            string path = PathOf(recipeName, flowName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            recipe.RecipeName = recipeName;
            recipe.FlowName = flowName;
            recipe.SavedAt = DateTime.Now;
            File.WriteAllText(path, recipe.Json);
        }

        public IEnumerable<string> ListRecipes()
        {
            if (!Directory.Exists(_rootDirectory))
            {
                return Enumerable.Empty<string>();
            }
            return Directory.GetDirectories(_rootDirectory).Select(Path.GetFileName);
        }

        public IEnumerable<string> ListFlows(string recipeName)
        {
            string directory = RecipeDirectory(recipeName);
            if (!Directory.Exists(directory))
            {
                return Enumerable.Empty<string>();
            }
            return Directory.GetFiles(directory, "*.vflow.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Select(name => name.EndsWith(".vflow", StringComparison.OrdinalIgnoreCase)
                    ? name.Substring(0, name.Length - ".vflow".Length)
                    : name);
        }

        private string PathOf(string recipeName, string flowName)
        {
            if (string.IsNullOrWhiteSpace(recipeName))
            {
                throw new ArgumentException("配方名称不能为空", nameof(recipeName));
            }
            if (string.IsNullOrWhiteSpace(flowName))
            {
                throw new ArgumentException("流程名称不能为空", nameof(flowName));
            }
            return Path.Combine(RecipeDirectory(recipeName), flowName + ".vflow.json");
        }

        private string RecipeDirectory(string recipeName)
        {
            return Path.Combine(_rootDirectory, Sanitize(recipeName));
        }

        private static string Sanitize(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }
            return value;
        }
    }
}
