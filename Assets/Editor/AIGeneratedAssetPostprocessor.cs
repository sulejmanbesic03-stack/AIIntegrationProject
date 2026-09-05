#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Import policy for assets handed off by AI Assistant's controlled Blender pipeline.
/// The desktop app copies exported assets to Assets/AI_Generated/Models.
/// Unity imports them normally; this postprocessor applies predictable model defaults.
/// </summary>
public sealed class AIGeneratedAssetPostprocessor : AssetPostprocessor
{
    private const string GeneratedRoot = "Assets/AI_Generated/";

    private void OnPreprocessModel()
    {
        if (!assetPath.StartsWith(GeneratedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (assetImporter is not ModelImporter importer)
        {
            return;
        }

        importer.globalScale = 1f;
        importer.importCameras = false;
        importer.importLights = false;
        importer.importVisibility = true;
        importer.importBlendShapes = true;
        importer.isReadable = false;
        importer.meshCompression = ModelImporterMeshCompression.Off;
        importer.optimizeMeshPolygons = true;
        importer.optimizeMeshVertices = true;
        importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
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

            if (path.EndsWith(".aiasset.json", StringComparison.OrdinalIgnoreCase))
            {
                Debug.Log("[AI Asset Pipeline] Manifest imported: " + path);
                continue;
            }

            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".fbx" || extension == ".glb" || extension == ".gltf")
            {
                Debug.Log("[AI Asset Pipeline] Generated model ready: " + path);
            }
        }
    }

    [MenuItem("AI Assistant/Generated Assets/Reveal Folder")]
    private static void RevealGeneratedFolder()
    {
        string absolute = Path.Combine(
            Directory.GetParent(Application.dataPath)!.FullName,
            "Assets",
            "AI_Generated",
            "Models"
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
}
#endif
