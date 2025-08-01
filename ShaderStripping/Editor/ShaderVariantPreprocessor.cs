using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;

using UnityEngine;
using UnityEngine.Rendering;

namespace ShaderStripping
{
    public class ShaderVariantPreprocessor : IPreprocessShaders
    {
        public int callbackOrder => 0;
        
        private string GetKeywordName(ShaderKeyword keyword)
        {
            return keyword.name;
        }
        
        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        {
            try
            {
                // Load all ShaderVariantPreprocessorSettings in the project, make variants go through them to check if they need to be stripped
                
                var preprocessorsGUIDs = AssetDatabase.FindAssets($"t:{nameof(ShaderVariantPreprocessorSettings)}");
                var preprocessors = preprocessorsGUIDs.Select(guid => AssetDatabase.LoadAssetAtPath<ShaderVariantPreprocessorSettings>(AssetDatabase.GUIDToAssetPath(guid))).ToArray();

                foreach (var preprocessor in preprocessors)
                {
                    if (preprocessor.strippingPasses == null || preprocessor.strippingPasses.Count == 0)
                        continue;
                    
                    // If not interested by that shader in any pass, continue to next preprocessor

                    if (preprocessor.strippingPasses.Exists(options => options.shaders.Contains(shader)) == false)
                        continue;
                    
                    foreach (var pass in preprocessor.strippingPasses)
                        pass.InitializeKeywordsSet();
                    
                    for (int i = data.Count - 1; i >= 0; i--)
                    {
                        string[] keywords = Array.ConvertAll(data[i].shaderKeywordSet.GetShaderKeywords(), GetKeywordName);
                        
                        // If no passes considers the variant as needed, strip it
                        bool shouldCompile = preprocessor.strippingPasses.Exists(options => options.ShouldCompileVariant(shader, ref snippet, keywords));
                        
                        if (!shouldCompile)
                            data.RemoveAt(i);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError(e);
            }
        }
    }
}
