using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

using Debug = UnityEngine.Debug;
using Task = System.Threading.Tasks.Task;

namespace ShaderStripping
{
    [CustomEditor(typeof(ShaderVariantCollectionBuilder))]
    public class ShaderVariantCollectionBuilderEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            var shaderVariantCollectionBuilder = target as ShaderVariantCollectionBuilder;

            if (shaderVariantCollectionBuilder)
            {
                if (GUILayout.Button("Build collection", GUILayout.Height(30)))
                {
                    shaderVariantCollectionBuilder.BuildCollection();
                }
            }
            
            GUILayout.Space(15);
            
            base.OnInspectorGUI();
        }
    }

    [CreateAssetMenu(menuName = "Shader Stripping/Shader Variant Collection Builder")]
    public class ShaderVariantCollectionBuilder : ScriptableObject
    {
        [Tooltip("Collection used to store all collected variants. Will be cleared before each run.")]
        public ShaderVariantCollection Collection;
        
        [Tooltip("The tool will only collect variants for materials using one of those shaders.")]
        public List<Shader> Shaders;
        
        private MethodInfo _ClearCurrentShaderVariantCollection;
        private MethodInfo _GetCurrentShaderVariantCollectionVariantCount;
        private MethodInfo _SaveCurrentShaderVariantCollection;
        
        private static string GetShaderVariantPreprocessorBaseDirectoryPath()
        {
            var g = AssetDatabase.FindAssets( $"t:Script {nameof(ShaderVariantCollectionBuilder)}" );
            return Path.GetDirectoryName(AssetDatabase.GUIDToAssetPath ( g [ 0 ] ));
        }
        
        private void ClearCurrentShaderVariantCollection()
        {
            _ClearCurrentShaderVariantCollection ??= typeof(ShaderUtil).GetMethod("ClearCurrentShaderVariantCollection",
                BindingFlags.Static | BindingFlags.NonPublic);
            _ClearCurrentShaderVariantCollection?.Invoke(null, Array.Empty<object>());
        }

        private int GetCurrentShaderVariantCollectionVariantCount()
        {
            _GetCurrentShaderVariantCollectionVariantCount ??= typeof(ShaderUtil).GetMethod("GetCurrentShaderVariantCollectionVariantCount",
                BindingFlags.Static | BindingFlags.NonPublic);
            return (int)_GetCurrentShaderVariantCollectionVariantCount?.Invoke(null, Array.Empty<object>())!;
        }
        
        private void SaveCurrentShaderVariantCollection(string path)
        {
            _SaveCurrentShaderVariantCollection ??= typeof(ShaderUtil).GetMethod("SaveCurrentShaderVariantCollection",
                BindingFlags.Static | BindingFlags.NonPublic);
            _SaveCurrentShaderVariantCollection?.Invoke(null, new object[] { path });
        }
        
        public async void BuildCollection()
        {
            string tempPath = Path.Combine(GetShaderVariantPreprocessorBaseDirectoryPath(), "Temp");

            if (Directory.Exists(tempPath) == false)
                Directory.CreateDirectory(tempPath);
            
            AssetDatabase.Refresh();

            // 1) Create temporary Prefab assets used to instantiate materials in a PrefabStage
            GameObject temp = new GameObject();
            GameObject tempInstance = GameObject.CreatePrimitive(PrimitiveType.Quad);
            DestroyImmediate(tempInstance.GetComponent<MeshCollider>());

            string tempPrefabPath = Path.Combine(tempPath, "ShaderVariantCollectionBuilder.prefab");
            string tempInstancePrefabPath = Path.Combine(tempPath, "ShaderVariantCollectionBuilderInstance.prefab");
            
            PrefabUtility.SaveAsPrefabAsset(temp, tempPrefabPath);
            var variantPrefab = PrefabUtility.SaveAsPrefabAsset(tempInstance, tempInstancePrefabPath);
            var shaderVariantPrefabMeshRenderer = variantPrefab.GetComponent<MeshRenderer>();
            
            DestroyImmediate(temp);
            DestroyImmediate(tempInstance);
            
            var prefabStage = PrefabStageUtility.OpenPrefab(tempPrefabPath);
            
            // 2) Look for all materials in the project using the right shader and instantiate a prefab with the material assigned
            
            var materialsGUIDs = AssetDatabase.FindAssets("t:Material");

            GameObject root = prefabStage.prefabContentsRoot;

            foreach (string guid in materialsGUIDs)
            {
                Material material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));

                if (Shaders.Contains(material.shader))
                {
                    var instance = Instantiate(shaderVariantPrefabMeshRenderer, root.transform);

                    instance.material = material;
                    instance.gameObject.name = material.name;
                }
            }

            SceneView.lastActiveSceneView?.Focus();
            
            // 3) Clear the engine collection, wait for the engine to reload the variants in memory (should only be those in the PrefabStage)
            
            ClearCurrentShaderVariantCollection();

            await Task.Delay(500);
            
            int variantCount = GetCurrentShaderVariantCollectionVariantCount();
            
            bool doneLoadingVariants = false;
            
            while (!doneLoadingVariants)
            {
                await Task.Delay(500);
                
                int currentVariantCount = GetCurrentShaderVariantCollectionVariantCount();
                
                if (currentVariantCount == variantCount)
                {
                    doneLoadingVariants = true;
                }
                else
                {
                    variantCount = currentVariantCount;
                }
            }
            
            // 4) Store the ShaderVariantCollection in a temporary asset, otherwise references to the previous collection are broken on import

            string tempCollectionPath = Path.Combine(tempPath, "ShaderVariantCollectionBuilder.shadervariants").Replace('\\', '/');
            
            SaveCurrentShaderVariantCollection(tempCollectionPath);

            await Task.Delay(200);
            
            var tempCollection = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(tempCollectionPath);
            
            // 5) Clear the final ShaderVariantCollection, copy over only the variants for shaders we care about
            
            Collection.Clear();

            foreach (var entry in GetCollectionEntries(tempCollection, Shaders))
            {
                Collection.Add(entry);
            }
            
            EditorUtility.SetDirty(Collection);
            AssetDatabase.SaveAssetIfDirty(Collection);
            
            // 6) Exit the PrefabStage, delete temporary assets

            StageUtility.GoBackToPreviousStage();
            
            AssetDatabase.DeleteAsset(tempPrefabPath);
            AssetDatabase.DeleteAsset(tempInstancePrefabPath);
            AssetDatabase.DeleteAsset(tempCollectionPath);
            
            Directory.Delete(tempPath);
        }

        public static List<ShaderVariantCollection.ShaderVariant> GetCollectionEntries(ShaderVariantCollection collection, List<Shader> shadersOfInterest)
        {
            // Use the SerializedObject API to look up the variant data, as it's not directly accessible through the ShaderVariantCollection API.
            try
            {
                List<ShaderVariantCollection.ShaderVariant> variants = new List<ShaderVariantCollection.ShaderVariant>();
                if (collection != null)
                {
                    SerializedObject serializedObject = new SerializedObject(collection);
                    var shaderProp = serializedObject.FindProperty("m_Shaders");

                    for (int shaderIndex = 0; shaderIndex < shaderProp.arraySize; ++shaderIndex)
                    {
                        var shaderEntry = shaderProp.GetArrayElementAtIndex(shaderIndex);
                        var shaderObjProp = shaderEntry.FindPropertyRelative("first");

                        var shader = (Shader)shaderObjProp.objectReferenceValue;

                        if (shadersOfInterest.Contains(shader) == false)
                            continue;
                        
                        var variantArrayProp = shaderEntry.FindPropertyRelative("second").FindPropertyRelative("variants");
                        
                        for (int variantIndex = 0; variantIndex < variantArrayProp.arraySize; ++variantIndex)
                        {
                            var variantEntry = variantArrayProp.GetArrayElementAtIndex(variantIndex);
                            var keywordsProp = variantEntry.FindPropertyRelative("keywords");
                            var passTypeProp = variantEntry.FindPropertyRelative("passType");

                            string[] keywords = keywordsProp.stringValue.Split(" ", StringSplitOptions.RemoveEmptyEntries);

                            for (int keywordIndex = 0; keywordIndex < keywords.Length; ++keywordIndex)
                            {
                                keywords[keywordIndex] = keywords[keywordIndex].Trim();
                            }
                            
                            variants.Add(new ((Shader)shaderObjProp.objectReferenceValue, (PassType)passTypeProp.intValue, keywords));
                        }
                    }
                }

                return variants;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Debug.LogError("Parsing failed. The ShaderVariantCollection file format may have changed.");
                return null;
            }
        }
    }
}
