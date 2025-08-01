using System;
using System.Collections.Generic;
using System.Linq;

using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShaderStripping
{
    [CreateAssetMenu(menuName = "Shader Stripping/Shader Variant Preprocessor Settings", fileName = nameof(ShaderVariantPreprocessorSettings))]
    public class ShaderVariantPreprocessorSettings : ScriptableObject
    {
        [Serializable]
        public class ShaderStrippingPass
        {
            [Tooltip("List of shaders to strip with those settings, ignore other shaders.")]
            public List<Shader> shaders = new();

            [Tooltip("List of pass types to strip, ignore other type of passes.")]
            public List<PassType> passTypes = new();
            
            [Tooltip("Extra keywords allowed to be compiled. Useful if variants with those keywords are not captured in the ShaderVariantCollections." +
                     "Note this can increase build times by a lot if this list grows too large.")]
            public List<string> extraKeywordsAllowed = new();

            [Tooltip("List of ShaderVariantCollection queried to allow a variant being compiled." +
                     "The variant only needs to be in one of the collection to get compiled.")]
            public List<ShaderVariantCollection> collections = new();

            private HashSet<string> extraKeywordsAllowedSet;

            public void InitializeKeywordsSet()
            {
                extraKeywordsAllowedSet = new(extraKeywordsAllowed);
            }
            
            public bool ShouldCompileVariant(Shader shader, ref ShaderSnippetData snippet, string[] keywords)
            {
                if (shaders.Contains(shader) == false)
                    return false;

                // If we don't care about the current PassType being compiled, don't strip anything 
                
                if (passTypes != null && passTypes.Count > 0 && passTypes.Contains(snippet.passType) == false)
                    return true;

                // Remove any extra keywords allowed from the set of keywords, then check for presence in a collection
                // If there's a match, the original variant with the extra keywords will be compiled
                // This is used for keywords not captured automatically and keywords enabled on materials at runtime
                
                string[] filteredKeywords = keywords.Where(kw => extraKeywordsAllowedSet.Contains(kw) == false).ToArray();
                
                ShaderVariantCollection.ShaderVariant variant;

                // Variants can fail to create when keywords are not compatible with the current pass being compiled
                // There seems to be an issue where some keywords from other passes are used for other passes when compiling URP shaders with Addressables

                try
                {
                    variant = new ShaderVariantCollection.ShaderVariant(shader, snippet.passType, filteredKeywords);
                }
                catch (Exception e)
                {
                    Debug.LogError($"{shader.name}/{snippet.passName} [{string.Join(" ", filteredKeywords)}]:\n{e.Message}");
                    return false;
                }

                return collections.Exists(collection => collection.Contains(variant));
            }
        }

        [Tooltip("List of stripping passes applied to compiled variants. Use separate passes to target each shaders differently.")]
        public List<ShaderStrippingPass> strippingPasses = new();
    }
}