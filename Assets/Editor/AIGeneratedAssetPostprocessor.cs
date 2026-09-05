#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Import policy and automatic handoff for AI-generated Blender assets.
/// V3 manifests prefer one complete Blender-authored FBX hierarchy. Unity
/// converts that hierarchy into a real prefab and places it 1:1 instead of
/// re-solving layout, scale or sub-object placement.
/// </summary>
public sealed class AIGeneratedAssetPostprocessor : AssetPostprocessor
{
    private const string GeneratedRoot = "Assets/AI_Generated/";
    private const string SceneManifestExtension = ".aiscene.json";

    private void OnPreprocessModel()
    {
        if (!assetPath.StartsWith(GeneratedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ModelImporter importer = assetImporter as ModelImporter;
        if (importer == null)
        {
            return;
        }

        importer.globalScale = 1f;
        importer.importCameras = false;
        importer.importLights = false;
        importer.importBlendShapes = true;
        importer.isReadable = false;
        importer.meshCompression = ModelImporterMeshCompression.Off;
        importer.optimizeMeshPolygons = true;
        importer.optimizeMeshVertices = true;
    }

    private static void OnPostprocessAllAssets(
        string[] importedAssets,
        string[] deletedAssets,
        string[] movedAssets,
        string[] movedFromAssetPaths
    )
    {
        foreach (string path in importedAssets)
        {
            if (!path.StartsWith(GeneratedRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (path.EndsWith(SceneManifestExtension, StringComparison.OrdinalIgnoreCase))
            {
                string capturedPath = path;
                EditorApplication.delayCall += () => BuildSceneFromManifest(capturedPath);
                continue;
            }

            if (path.EndsWith(".aiasset.json", StringComparison.OrdinalIgnoreCase))
            {
                Debug.Log("[AI Asset Pipeline] Manifest imported: " + path);
                continue;
            }

            if (path.EndsWith(".topology.json", StringComparison.OrdinalIgnoreCase))
            {
                Debug.Log("[AI Asset Pipeline] Topology report imported: " + path);
                continue;
            }

            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".fbx" || extension == ".glb" || extension == ".gltf")
            {
                Debug.Log("[AI Asset Pipeline] Generated model ready: " + path);
            }
        }
    }

    private static void BuildSceneFromManifest(string manifestAssetPath)
    {
        try
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                Debug.LogError("[AI Asset Pipeline] Could not resolve project root for scene manifest.");
                return;
            }

            string absoluteManifest = Path.Combine(
                projectRoot,
                manifestAssetPath.Replace('/', Path.DirectorySeparatorChar)
            );

            if (!File.Exists(absoluteManifest))
            {
                Debug.LogWarning("[AI Asset Pipeline] Scene manifest disappeared before assembly: " + manifestAssetPath);
                return;
            }

            GeneratedSceneManifest manifest = JsonUtility.FromJson<GeneratedSceneManifest>(
                File.ReadAllText(absoluteManifest)
            );

            if (manifest == null)
            {
                Debug.LogWarning("[AI Asset Pipeline] Scene manifest could not be parsed: " + manifestAssetPath);
                return;
            }

            if (!string.IsNullOrWhiteSpace(manifest.prefabAssetPath))
            {
                BuildPrefabBundle(manifest, manifestAssetPath);
                return;
            }

            if (manifest.instances == null || manifest.instances.Length == 0)
            {
                Debug.LogWarning("[AI Asset Pipeline] Scene manifest contains no prefab bundle or instances: " + manifestAssetPath);
                return;
            }

            BuildLegacyInstanceScene(manifest, manifestAssetPath);
        }
        catch (Exception ex)
        {
            Debug.LogError(
                "[AI Asset Pipeline] Scene manifest assembly failed: "
                + ex.GetType().Name
                + ": "
                + ex.Message
            );
        }
    }

    private static void BuildPrefabBundle(
        GeneratedSceneManifest manifest,
        string manifestAssetPath
    )
    {
        AssetDatabase.ImportAsset(
            manifest.prefabAssetPath,
            ImportAssetOptions.ForceSynchronousImport
        );

        GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(
            manifest.prefabAssetPath
        );

        if (model == null)
        {
            Debug.LogError(
                "[AI Asset Pipeline] Blender-authored scene bundle is not ready: "
                + manifest.prefabAssetPath
            );
            return;
        }

        string rootName = string.IsNullOrWhiteSpace(manifest.rootName)
            ? "AI_Generated_Scene"
            : manifest.rootName;

        string prefabPath = string.IsNullOrWhiteSpace(manifest.prefabOutputPath)
            ? "Assets/AI_Generated/Prefabs/" + SanitizeFileName(rootName) + ".prefab"
            : manifest.prefabOutputPath;

        string prefabDirectory = Path.GetDirectoryName(prefabPath)?.Replace('\\', '/');
        EnsureAssetFolder(prefabDirectory);

        GameObject temporary = PrefabUtility.InstantiatePrefab(model) as GameObject;
        if (temporary == null)
        {
            temporary = UnityEngine.Object.Instantiate(model);
        }

        temporary.name = rootName;
        temporary.transform.position = Vector3.zero;
        temporary.transform.rotation = Quaternion.identity;
        temporary.transform.localScale = Vector3.one;

        GameObject savedPrefab = PrefabUtility.SaveAsPrefabAsset(
            temporary,
            prefabPath
        );
        UnityEngine.Object.DestroyImmediate(temporary);

        if (savedPrefab == null)
        {
            Debug.LogError(
                "[AI Asset Pipeline] Could not create Unity prefab from Blender scene bundle: "
                + prefabPath
            );
            return;
        }

        GameObject existingRoot = FindSceneObject(rootName);
        if (existingRoot != null)
        {
            if (!manifest.replaceExisting)
            {
                Debug.Log(
                    "[AI Asset Pipeline] Existing generated scene root retained: "
                    + rootName
                );
                return;
            }

            UnityEngine.Object.DestroyImmediate(existingRoot);
        }

        GameObject created = PrefabUtility.InstantiatePrefab(savedPrefab) as GameObject;
        if (created == null)
        {
            created = UnityEngine.Object.Instantiate(savedPrefab);
        }

        created.name = rootName;
        created.transform.position = Vector3.zero;
        created.transform.rotation = Quaternion.identity;
        created.transform.localScale = Vector3.one;

        Undo.RegisterCreatedObjectUndo(created, "AI Generated Blender Prefab Scene");
        Selection.activeGameObject = created;
        EditorSceneManager.MarkSceneDirty(created.scene);
        EditorSceneManager.SaveOpenScenes();

        Debug.Log(
            "[AI Asset Pipeline] Blender-authored scene imported 1:1 as prefab: "
            + prefabPath
            + " | source=" + manifest.prefabAssetPath
            + " | manifest=" + manifestAssetPath
        );
    }

    private static void BuildLegacyInstanceScene(
        GeneratedSceneManifest manifest,
        string manifestAssetPath
    )
    {
        string rootName = string.IsNullOrWhiteSpace(manifest.rootName)
            ? "AI_Generated_Scene"
            : manifest.rootName;

        GameObject existingRoot = FindSceneObject(rootName);
        if (existingRoot != null)
        {
            if (!manifest.replaceExisting)
            {
                Debug.Log(
                    "[AI Asset Pipeline] Existing generated scene root retained: "
                    + rootName
                );
                return;
            }

            UnityEngine.Object.DestroyImmediate(existingRoot);
        }

        GameObject sceneRoot = new GameObject(rootName);
        int instantiated = 0;
        int missing = 0;

        foreach (GeneratedSceneInstance instance in manifest.instances)
        {
            if (instance == null || string.IsNullOrWhiteSpace(instance.assetPath))
            {
                missing++;
                continue;
            }

            AssetDatabase.ImportAsset(
                instance.assetPath,
                ImportAssetOptions.ForceSynchronousImport
            );

            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(
                instance.assetPath
            );

            if (model == null)
            {
                missing++;
                continue;
            }

            GameObject created = PrefabUtility.InstantiatePrefab(model) as GameObject;
            if (created == null)
            {
                created = UnityEngine.Object.Instantiate(model);
            }

            created.name = string.IsNullOrWhiteSpace(instance.name)
                ? model.name
                : instance.name;
            created.transform.SetParent(sceneRoot.transform, false);
            created.transform.localPosition = ReadVector(instance.position, Vector3.zero);
            created.transform.localEulerAngles = ReadVector(instance.rotation, Vector3.zero);
            created.transform.localScale = Vector3.one;
            instantiated++;
        }

        if (instantiated == 0)
        {
            UnityEngine.Object.DestroyImmediate(sceneRoot);
            Debug.LogError("[AI Asset Pipeline] Legacy scene assembly failed: no generated model could be instantiated.");
            return;
        }

        Undo.RegisterCreatedObjectUndo(sceneRoot, "AI Generated Scene");
        Selection.activeGameObject = sceneRoot;
        EditorSceneManager.MarkSceneDirty(sceneRoot.scene);
        EditorSceneManager.SaveOpenScenes();

        Debug.Log(
            "[AI Asset Pipeline] Legacy scene assembled: "
            + rootName
            + " | instances=" + instantiated
            + " | missing=" + missing
            + " | manifest=" + manifestAssetPath
        );
    }

    private static void EnsureAssetFolder(string assetFolder)
    {
        if (string.IsNullOrWhiteSpace(assetFolder)
            || assetFolder.Equals("Assets", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string normalized = assetFolder.Replace('\\', '/');
        string[] parts = normalized.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
            {
                AssetDatabase.CreateFolder(current, parts[i]);
            }
            current = next;
        }
    }

    private static string SanitizeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }
        return string.IsNullOrWhiteSpace(value) ? "AI_Generated_Scene" : value;
    }

    private static GameObject FindSceneObject(string name)
    {
        return Resources.FindObjectsOfTypeAll<GameObject>()
            .FirstOrDefault(candidate =>
                candidate != null
                && candidate.scene.IsValid()
                && candidate.scene.isLoaded
                && candidate.name.Equals(name, StringComparison.Ordinal)
            );
    }

    private static Vector3 ReadVector(
        float[] values,
        Vector3 fallback,
        bool protectZeroScale = false
    )
    {
        if (values == null || values.Length < 3)
        {
            return fallback;
        }

        Vector3 result = new Vector3(values[0], values[1], values[2]);
        if (protectZeroScale)
        {
            if (Mathf.Abs(result.x) < 0.0001f) result.x = 1f;
            if (Mathf.Abs(result.y) < 0.0001f) result.y = 1f;
            if (Mathf.Abs(result.z) < 0.0001f) result.z = 1f;
        }

        return result;
    }

    [MenuItem("AI Assistant/Generated Assets/Reveal Folder")]
    private static void RevealGeneratedFolder()
    {
        DirectoryInfo projectDirectory = Directory.GetParent(Application.dataPath);
        if (projectDirectory == null)
        {
            Debug.LogWarning("[AI Asset Pipeline] Could not resolve Unity project root.");
            return;
        }

        string absolute = Path.Combine(
            projectDirectory.FullName,
            "Assets",
            "AI_Generated"
        );
        Directory.CreateDirectory(absolute);
        EditorUtility.RevealInFinder(absolute);
    }

    [MenuItem("AI Assistant/Generated Assets/Reimport All")]
    private static void ReimportAll()
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        Debug.Log("[AI Asset Pipeline] Generated assets refreshed.");
    }

    [Serializable]
    private sealed class GeneratedSceneManifest
    {
        public int version;
        public string sceneName;
        public string rootName;
        public bool replaceExisting = true;
        public string prefabAssetPath;
        public string prefabOutputPath;
        public GeneratedSceneInstance[] instances;
    }

    [Serializable]
    private sealed class GeneratedSceneInstance
    {
        public string assetPath;
        public string name;
        public float[] position;
        public float[] rotation;
        public float[] scale;
    }
}
#endif
