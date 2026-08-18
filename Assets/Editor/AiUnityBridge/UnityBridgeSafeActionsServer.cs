using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class UnityBridgeSafeActionsServer
{
    private const int Port = 47823;

    private const string RequiredHeaderValue =
        "AI-Assistant-Local";

    private static readonly ConcurrentQueue<Request> pending =
        new ConcurrentQueue<Request>();

    private static TcpListener listener;

    private static volatile bool running;

    static UnityBridgeSafeActionsServer()
    {
        EditorApplication.update +=
            ProcessPending;

        AssemblyReloadEvents.beforeAssemblyReload +=
            Stop;

        EditorApplication.quitting +=
            Stop;

        Start();
    }

    // ============================================
    // SERVER START / STOP
    // ============================================

    private static void Start()
    {
        if (running)
        {
            return;
        }

        try
        {
            listener =
                new TcpListener(
                    IPAddress.Loopback,
                    Port
                );

            listener.Start();

            running = true;

            Thread thread =
                new Thread(
                    ListenLoop
                );

            thread.IsBackground = true;

            thread.Name =
                "AI Unity Safe Actions";

            thread.Start();

            Debug.Log(
                $"AI Unity Safe Actions sluša na http://127.0.0.1:{Port}/"
            );
        }
        catch (Exception ex)
        {
            running = false;

            Debug.LogError(
                $"AI Unity Safe Actions nije pokrenut: {ex.Message}"
            );
        }
    }

    private static void Stop()
    {
        running = false;

        try
        {
            listener?.Stop();
        }
        catch
        {
            // Unity se gasi ili ponovo učitava skripte.
        }
    }

    // ============================================
    // NETWORK THREAD
    // ============================================

    private static void ListenLoop()
    {
        while (running)
        {
            try
            {
                TcpClient client =
                    listener.AcceptTcpClient();

                ThreadPool.QueueUserWorkItem(
                    _ => ReadAndQueue(
                        client
                    )
                );
            }
            catch (SocketException)
            {
                if (running)
                {
                    Debug.LogWarning(
                        "AI Unity Safe Actions listener stopped."
                    );
                }
            }
            catch (Exception ex)
            {
                if (running)
                {
                    Debug.LogWarning(
                        ex.Message
                    );
                }
            }
        }
    }

    private static void ReadAndQueue(
        TcpClient client
    )
    {
        try
        {
            pending.Enqueue(
                ReadRequest(
                    client
                )
            );
        }
        catch (IOException)
        {
            client.Dispose();
        }
        catch (SocketException)
        {
            client.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Server ili klijent je zatvorio konekciju.
        }
        catch (Exception ex)
        {
            client.Dispose();

            Debug.LogWarning(
                $"AI Unity Safe Actions rejected a request: {ex.Message}"
            );
        }
    }

    // ============================================
    // UNITY MAIN THREAD
    // ============================================

    private static void ProcessPending()
    {
        while (
            pending.TryDequeue(
                out Request request
            )
        )
        {
            Process(
                request
            );
        }
    }

    private static void Process(
        Request request
    )
    {
        try
        {
            if (
                !request.Method.Equals(
                    "POST",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                WriteError(
                    request.Client,
                    405,
                    "Only POST is supported."
                );

                return;
            }

            if (
                !request.Header.Equals(
                    RequiredHeaderValue,
                    StringComparison.Ordinal
                )
            )
            {
                WriteError(
                    request.Client,
                    403,
                    "Invalid X-AI-Bridge header."
                );

                return;
            }

            string path =
                request.Path
                    .TrimEnd('/')
                    .ToLowerInvariant();

            switch (path)
            {
                case "/add-component":

                    AddComponent(
                        request,
                        false
                    );

                    return;

                case "/attach-script":

                    AddComponent(
                        request,
                        true
                    );

                    return;

                case "/save-scene":

                    SaveScene(
                        request
                    );

                    return;

                case "/create-primitive":

                    CreatePrimitive(
                        request
                    );

                    return;

                case "/rename-gameobject":

                    RenameGameObject(
                        request
                    );

                    return;

                case "/set-parent":

                    SetParent(
                        request
                    );

                    return;

                case "/set-active":

                    SetActive(
                        request
                    );

                    return;

                case "/find-assets":

                    FindAssets(
                        request
                    );

                    return;

                case "/get-asset-info":

                    GetAssetInfo(
                        request
                    );

                    return;

                case "/create-material":

                    CreateMaterial(
                        request
                    );

                    return;

                case "/set-material-color":

                    SetMaterialColor(
                        request
                    );

                    return;

                case "/assign-material":

                    AssignMaterial(
                        request
                    );

                    return;

                case "/import-asset":

                    ImportAsset(
                        request
                    );

                    return;

                case "/set-position":

                    SetVector(
                        request,
                        "position"
                    );

                    return;

                case "/set-rotation":

                    SetVector(
                        request,
                        "rotation"
                    );

                    return;

                case "/set-scale":

                    SetVector(
                        request,
                        "scale"
                    );

                    return;

                case "/duplicate-gameobject":

                    DuplicateGameObject(
                        request
                    );

                    return;

                case "/configure-rigidbody":

                    ConfigureRigidbody(
                        request
                    );

                    return;

                case "/configure-collider":

                    ConfigureCollider(
                        request
                    );

                    return;

                case "/create-prefab":

                    CreatePrefab(
                        request
                    );

                    return;

                case "/instantiate-prefab":

                    InstantiatePrefab(
                        request
                    );

                    return;

                default:

                    WriteError(
                        request.Client,
                        404,
                        $"Unknown endpoint: {path}"
                    );

                    return;
            }
        }
        catch (Exception ex)
        {
            try
            {
                WriteError(
                    request.Client,
                    500,
                    ex.Message
                );
            }
            catch
            {
                // Klijent je zatvorio konekciju.
            }
        }
    }

    // ============================================
    // COMPONENTS
    // ============================================

    private static void AddComponent(
        Request request,
        bool scriptsOnly
    )
    {
        ComponentRequest data =
            JsonUtility.FromJson<ComponentRequest>(
                request.Body
            );

        GameObject gameObject =
            Find(
                data.objectPath
            );

        if (gameObject == null)
        {
            WriteError(
                request.Client,
                404,
                $"GameObject not found: {data.objectPath}"
            );

            return;
        }

        Type type =
            ResolveComponentType(
                data.componentType,
                scriptsOnly
            );

        if (type == null)
        {
            WriteError(
                request.Client,
                404,
                $"Component type not found or not allowed: {data.componentType}"
            );

            return;
        }

        if (type == typeof(Transform))
        {
            WriteError(
                request.Client,
                400,
                "Transform cannot be added manually."
            );

            return;
        }

        if (gameObject.GetComponent(type) != null)
        {
            WriteError(
                request.Client,
                400,
                $"Component already exists: {type.FullName}"
            );

            return;
        }

        Component component =
            Undo.AddComponent(
                gameObject,
                type
            );

        EditorSceneManager.MarkSceneDirty(
            gameObject.scene
        );

        WriteOk(
            request.Client,
            $"Added {component.GetType().FullName} to {BuildPath(gameObject.transform)}"
        );
    }

    private static Type ResolveComponentType(
        string name,
        bool scriptsOnly
    )
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return TypeCache
            .GetTypesDerivedFrom<Component>()
            .Where(
                type => !type.IsAbstract
            )
            .Where(
                type =>
                    !scriptsOnly
                    ||
                    typeof(MonoBehaviour)
                        .IsAssignableFrom(type)
            )
            .FirstOrDefault(
                type =>
                    type.Name.Equals(
                        name,
                        StringComparison.OrdinalIgnoreCase
                    )
                    ||
                    string.Equals(
                        type.FullName,
                        name,
                        StringComparison.OrdinalIgnoreCase
                    )
            );
    }

    // ============================================
    // SAVE SCENE
    // ============================================

    private static void SaveScene(
        Request request
    )
    {
        Scene scene =
            SceneManager.GetActiveScene();

        if (string.IsNullOrWhiteSpace(scene.path))
        {
            WriteError(
                request.Client,
                400,
                "Active scene has no path. Save it manually once first."
            );

            return;
        }

        bool saved =
            EditorSceneManager.SaveScene(
                scene
            );

        if (!saved)
        {
            WriteError(
                request.Client,
                500,
                "Unity could not save the active scene."
            );

            return;
        }

        WriteOk(
            request.Client,
            $"Saved scene: {scene.path}"
        );
    }

    // ============================================
    // CREATE PRIMITIVE
    // ============================================

    private static void CreatePrimitive(
        Request request
    )
    {
        PrimitiveRequest data =
            JsonUtility.FromJson<PrimitiveRequest>(
                request.Body
            );

        if (
            !Enum.TryParse(
                data.primitiveType,
                true,
                out PrimitiveType primitiveType
            )
        )
        {
            WriteError(
                request.Client,
                400,
                "primitiveType must be Cube, Sphere, Capsule, Cylinder, Plane or Quad."
            );

            return;
        }

        GameObject parent =
            string.IsNullOrWhiteSpace(data.parentPath)
                ? null
                : Find(data.parentPath);

        if (
            !string.IsNullOrWhiteSpace(data.parentPath)
            &&
            parent == null
        )
        {
            WriteError(
                request.Client,
                404,
                $"Parent not found: {data.parentPath}"
            );

            return;
        }

        GameObject created =
            GameObject.CreatePrimitive(
                primitiveType
            );

        created.name =
            string.IsNullOrWhiteSpace(data.name)
                ? primitiveType.ToString()
                : data.name.Trim();

        Undo.RegisterCreatedObjectUndo(
            created,
            $"AI Create {primitiveType}"
        );

        if (parent != null)
        {
            Undo.SetTransformParent(
                created.transform,
                parent.transform,
                "AI Set Parent"
            );
        }

        EditorSceneManager.MarkSceneDirty(
            created.scene
        );

        WriteOk(
            request.Client,
            $"Created {primitiveType}: {BuildPath(created.transform)}"
        );
    }

    // ============================================
    // RENAME / PARENT / ACTIVE
    // ============================================

    private static void RenameGameObject(
        Request request
    )
    {
        RenameRequest data =
            JsonUtility.FromJson<RenameRequest>(
                request.Body
            );

        GameObject gameObject =
            Find(
                data.objectPath
            );

        if (gameObject == null)
        {
            WriteError(
                request.Client,
                404,
                $"GameObject not found: {data.objectPath}"
            );

            return;
        }

        if (string.IsNullOrWhiteSpace(data.newName))
        {
            WriteError(
                request.Client,
                400,
                "newName is required."
            );

            return;
        }

        Undo.RecordObject(
            gameObject,
            "AI Rename GameObject"
        );

        gameObject.name =
            data.newName.Trim();

        EditorSceneManager.MarkSceneDirty(
            gameObject.scene
        );

        WriteOk(
            request.Client,
            $"Renamed GameObject: {BuildPath(gameObject.transform)}"
        );
    }

    private static void SetParent(
        Request request
    )
    {
        ParentRequest data =
            JsonUtility.FromJson<ParentRequest>(
                request.Body
            );

        GameObject gameObject =
            Find(
                data.objectPath
            );

        if (gameObject == null)
        {
            WriteError(
                request.Client,
                404,
                $"GameObject not found: {data.objectPath}"
            );

            return;
        }

        GameObject parent =
            string.IsNullOrWhiteSpace(data.parentPath)
                ? null
                : Find(data.parentPath);

        if (
            !string.IsNullOrWhiteSpace(data.parentPath)
            &&
            parent == null
        )
        {
            WriteError(
                request.Client,
                404,
                $"Parent not found: {data.parentPath}"
            );

            return;
        }

        if (
            parent == gameObject
            ||
            (
                parent != null
                &&
                parent.transform.IsChildOf(
                    gameObject.transform
                )
            )
        )
        {
            WriteError(
                request.Client,
                400,
                "Invalid circular parent relationship."
            );

            return;
        }

        Undo.SetTransformParent(
            gameObject.transform,
            parent == null
                ? null
                : parent.transform,
            "AI Set Parent"
        );

        EditorSceneManager.MarkSceneDirty(
            gameObject.scene
        );

        WriteOk(
            request.Client,
            $"New hierarchy path: {BuildPath(gameObject.transform)}"
        );
    }

    private static void SetActive(
        Request request
    )
    {
        ActiveRequest data =
            JsonUtility.FromJson<ActiveRequest>(
                request.Body
            );

        GameObject gameObject =
            Find(
                data.objectPath
            );

        if (gameObject == null)
        {
            WriteError(
                request.Client,
                404,
                $"GameObject not found: {data.objectPath}"
            );

            return;
        }

        Undo.RecordObject(
            gameObject,
            "AI Set Active"
        );

        gameObject.SetActive(
            data.active
        );

        EditorSceneManager.MarkSceneDirty(
            gameObject.scene
        );

        WriteOk(
            request.Client,
            $"Set {BuildPath(gameObject.transform)} active={data.active}"
        );
    }

    // ============================================
    // ASSETS
    // ============================================

    private static void FindAssets(
        Request request
    )
    {
        FindAssetsRequest data =
            JsonUtility.FromJson<FindAssetsRequest>(
                request.Body
            );

        string folder =
            string.IsNullOrWhiteSpace(data.searchFolder)
                ? "Assets"
                : NormalizeAssetPath(
                    data.searchFolder,
                    false
                );

        if (!AssetDatabase.IsValidFolder(folder))
        {
            WriteError(
                request.Client,
                404,
                $"Asset folder not found: {folder}"
            );

            return;
        }

        string[] guids =
            AssetDatabase.FindAssets(
                data.filter ?? "",
                new[]
                {
                    folder
                }
            );

        string[] paths =
            guids
                .Take(50)
                .Select(
                    AssetDatabase.GUIDToAssetPath
                )
                .ToArray();

        AssetSearchResponse response =
            new AssetSearchResponse
            {
                success = true,
                count = paths.Length,
                paths = paths
            };

        Write(
            request.Client,
            200,
            JsonUtility.ToJson(
                response,
                true
            )
        );
    }

    private static void GetAssetInfo(
        Request request
    )
    {
        AssetPathRequest data =
            JsonUtility.FromJson<AssetPathRequest>(
                request.Body
            );

        string path =
            NormalizeAssetPath(
                data.assetPath,
                false
            );

        UnityEngine.Object asset =
            AssetDatabase.LoadMainAssetAtPath(
                path
            );

        if (asset == null)
        {
            WriteError(
                request.Client,
                404,
                $"Asset not found: {path}"
            );

            return;
        }

        AssetInfoResponse response =
            new AssetInfoResponse
            {
                success = true,
                path = path,
                name = asset.name,
                type = asset.GetType().FullName
            };

        Write(
            request.Client,
            200,
            JsonUtility.ToJson(
                response,
                true
            )
        );
    }

    // ============================================
    // MATERIALS
    // ============================================

    private static void CreateMaterial(
        Request request
    )
    {
        CreateMaterialRequest data =
            JsonUtility.FromJson<CreateMaterialRequest>(
                request.Body
            );

        string path =
            NormalizeAssetPath(
                data.assetPath,
                true
            );

        if (
            !path.EndsWith(
                ".mat",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            WriteError(
                request.Client,
                400,
                "Material path must end with .mat"
            );

            return;
        }

        if (
            AssetDatabase.LoadMainAssetAtPath(path)
            != null
        )
        {
            WriteError(
                request.Client,
                400,
                $"Asset already exists: {path}"
            );

            return;
        }

        string shaderName =
            string.IsNullOrWhiteSpace(data.shaderName)
                ? "Universal Render Pipeline/Lit"
                : data.shaderName;

        Shader shader =
            Shader.Find(
                shaderName
            );

        if (shader == null)
        {
            WriteError(
                request.Client,
                404,
                $"Shader not found: {shaderName}"
            );

            return;
        }

        EnsureAssetDirectory(
            path
        );

        Material material =
            new Material(
                shader
            );

        AssetDatabase.CreateAsset(
            material,
            path
        );

        AssetDatabase.SaveAssets();

        WriteOk(
            request.Client,
            $"Created material: {path}"
        );
    }

    private static void SetMaterialColor(
        Request request
    )
    {
        MaterialColorRequest data =
            JsonUtility.FromJson<MaterialColorRequest>(
                request.Body
            );

        string path =
            NormalizeAssetPath(
                data.materialPath,
                false
            );

        Material material =
            AssetDatabase.LoadAssetAtPath<Material>(
                path
            );

        if (material == null)
        {
            WriteError(
                request.Client,
                404,
                $"Material not found: {path}"
            );

            return;
        }

        Undo.RecordObject(
            material,
            "AI Set Material Color"
        );

        Color color =
            new Color(
                Mathf.Clamp01(data.red),
                Mathf.Clamp01(data.green),
                Mathf.Clamp01(data.blue),
                Mathf.Clamp01(data.alpha)
            );

        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor(
                "_BaseColor",
                color
            );
        }
        else if (material.HasProperty("_Color"))
        {
            material.SetColor(
                "_Color",
                color
            );
        }
        else
        {
            WriteError(
                request.Client,
                400,
                "Material shader has no supported color property."
            );

            return;
        }

        EditorUtility.SetDirty(
            material
        );

        AssetDatabase.SaveAssets();

        WriteOk(
            request.Client,
            $"Updated material color: {path}"
        );
    }

    private static void AssignMaterial(
        Request request
    )
    {
        AssignMaterialRequest data =
            JsonUtility.FromJson<AssignMaterialRequest>(
                request.Body
            );

        GameObject gameObject =
            Find(
                data.objectPath
            );

        if (gameObject == null)
        {
            WriteError(
                request.Client,
                404,
                $"GameObject not found: {data.objectPath}"
            );

            return;
        }

        string path =
            NormalizeAssetPath(
                data.materialPath,
                false
            );

        Material material =
            AssetDatabase.LoadAssetAtPath<Material>(
                path
            );

        if (material == null)
        {
            WriteError(
                request.Client,
                404,
                $"Material not found: {path}"
            );

            return;
        }

        Renderer renderer =
            gameObject.GetComponent<Renderer>();

        if (renderer == null)
        {
            WriteError(
                request.Client,
                400,
                $"GameObject has no Renderer: {data.objectPath}"
            );

            return;
        }

        Undo.RecordObject(
            renderer,
            "AI Assign Material"
        );

        renderer.sharedMaterial =
            material;

        EditorUtility.SetDirty(
            renderer
        );

        EditorSceneManager.MarkSceneDirty(
            gameObject.scene
        );

        WriteOk(
            request.Client,
            $"Assigned {path} to {BuildPath(gameObject.transform)}"
        );
    }

    private static void ImportAsset(
        Request request
    )
    {
        AssetPathRequest data =
            JsonUtility.FromJson<AssetPathRequest>(
                request.Body
            );

        string path =
            NormalizeAssetPath(
                data.assetPath,
                false
            );

        string projectRoot =
            Directory
                .GetParent(Application.dataPath)
                .FullName;

        string absolute =
            Path.GetFullPath(
                Path.Combine(
                    projectRoot,
                    path
                )
            );

        if (
            !File.Exists(absolute)
            &&
            !Directory.Exists(absolute)
        )
        {
            WriteError(
                request.Client,
                404,
                $"File not found inside project: {path}"
            );

            return;
        }

        AssetDatabase.ImportAsset(
            path,
            ImportAssetOptions.ForceUpdate
        );

        WriteOk(
            request.Client,
            $"Imported asset: {path}"
        );
    }

    // ============================================
    // TRANSFORM
    // ============================================

    private static void SetVector(
        Request request,
        string mode
    )
    {
        VectorRequest data =
            JsonUtility.FromJson<VectorRequest>(
                request.Body
            );

        GameObject gameObject =
            Find(
                data.objectPath
            );

        if (gameObject == null)
        {
            WriteError(
                request.Client,
                404,
                $"GameObject not found: {data.objectPath}"
            );

            return;
        }

        Transform transform =
            gameObject.transform;

        Undo.RecordObject(
            transform,
            $"AI Set {mode}"
        );

        Vector3 value =
            new Vector3(
                data.x,
                data.y,
                data.z
            );

        if (mode == "position")
        {
            transform.position =
                value;
        }
        else if (mode == "rotation")
        {
            transform.eulerAngles =
                value;
        }
        else
        {
            transform.localScale =
                value;
        }

        EditorUtility.SetDirty(
            transform
        );

        EditorSceneManager.MarkSceneDirty(
            gameObject.scene
        );

        WriteOk(
            request.Client,
            $"Set {mode} on {BuildPath(transform)} to ({data.x}, {data.y}, {data.z})"
        );
    }

    // ============================================
    // DUPLICATE
    // ============================================

    private static void DuplicateGameObject(
        Request request
    )
    {
        DuplicateRequest data =
            JsonUtility.FromJson<DuplicateRequest>(
                request.Body
            );

        GameObject source =
            Find(
                data.objectPath
            );

        if (source == null)
        {
            WriteError(
                request.Client,
                404,
                $"GameObject not found: {data.objectPath}"
            );

            return;
        }

        GameObject parent =
            string.IsNullOrWhiteSpace(data.parentPath)
                ? null
                : Find(data.parentPath);

        if (
            !string.IsNullOrWhiteSpace(data.parentPath)
            &&
            parent == null
        )
        {
            WriteError(
                request.Client,
                404,
                $"Parent not found: {data.parentPath}"
            );

            return;
        }

        GameObject copy =
            UnityEngine.Object.Instantiate(
                source,
                parent == null
                    ? null
                    : parent.transform
            );

        copy.name =
            string.IsNullOrWhiteSpace(data.newName)
                ? source.name + " Copy"
                : data.newName.Trim();

        Undo.RegisterCreatedObjectUndo(
            copy,
            "AI Duplicate GameObject"
        );

        EditorSceneManager.MarkSceneDirty(
            copy.scene
        );

        WriteOk(
            request.Client,
            $"Duplicated GameObject: {BuildPath(copy.transform)}"
        );
    }

    // ============================================
    // RIGIDBODY
    // ============================================

    private static void ConfigureRigidbody(
        Request request
    )
    {
        RigidbodyRequest data =
            JsonUtility.FromJson<RigidbodyRequest>(
                request.Body
            );

        GameObject gameObject =
            Find(
                data.objectPath
            );

        if (gameObject == null)
        {
            WriteError(
                request.Client,
                404,
                $"GameObject not found: {data.objectPath}"
            );

            return;
        }

        Rigidbody body =
            gameObject.GetComponent<Rigidbody>();

        if (body == null)
        {
            WriteError(
                request.Client,
                404,
                $"Rigidbody not found on: {data.objectPath}"
            );

            return;
        }

        if (data.mass <= 0f)
        {
            WriteError(
                request.Client,
                400,
                "mass must be greater than zero."
            );

            return;
        }

        Undo.RecordObject(
            body,
            "AI Configure Rigidbody"
        );

        body.mass =
            data.mass;

        body.useGravity =
            data.useGravity;

        body.isKinematic =
            data.isKinematic;

        EditorUtility.SetDirty(
            body
        );

        EditorSceneManager.MarkSceneDirty(
            gameObject.scene
        );

        WriteOk(
            request.Client,
            $"Configured Rigidbody on {BuildPath(gameObject.transform)}"
        );
    }

    // ============================================
    // COLLIDER
    // ============================================

    private static void ConfigureCollider(
        Request request
    )
    {
        ColliderRequest data =
            JsonUtility.FromJson<ColliderRequest>(
                request.Body
            );

        GameObject gameObject =
            Find(
                data.objectPath
            );

        if (gameObject == null)
        {
            WriteError(
                request.Client,
                404,
                $"GameObject not found: {data.objectPath}"
            );

            return;
        }

        Collider collider =
            gameObject.GetComponent<Collider>();

        if (collider == null)
        {
            WriteError(
                request.Client,
                404,
                $"Collider not found on: {data.objectPath}"
            );

            return;
        }

        Undo.RecordObject(
            collider,
            "AI Configure Collider"
        );

        collider.enabled =
            data.enabled;

        collider.isTrigger =
            data.isTrigger;

        EditorUtility.SetDirty(
            collider
        );

        EditorSceneManager.MarkSceneDirty(
            gameObject.scene
        );

        WriteOk(
            request.Client,
            $"Configured {collider.GetType().Name} on {BuildPath(gameObject.transform)}"
        );
    }

    // ============================================
    // PREFABS
    // ============================================

    private static void CreatePrefab(
        Request request
    )
    {
        PrefabCreateRequest data =
            JsonUtility.FromJson<PrefabCreateRequest>(
                request.Body
            );

        GameObject gameObject =
            Find(
                data.objectPath
            );

        if (gameObject == null)
        {
            WriteError(
                request.Client,
                404,
                $"GameObject not found: {data.objectPath}"
            );

            return;
        }

        string path =
            NormalizeAssetPath(
                data.assetPath,
                true
            );

        if (
            !path.EndsWith(
                ".prefab",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            WriteError(
                request.Client,
                400,
                "Prefab path must end with .prefab"
            );

            return;
        }

        if (
            AssetDatabase.LoadMainAssetAtPath(path)
            != null
        )
        {
            WriteError(
                request.Client,
                400,
                $"Asset already exists: {path}"
            );

            return;
        }

        EnsureAssetDirectory(
            path
        );

        GameObject prefab =
            PrefabUtility.SaveAsPrefabAsset(
                gameObject,
                path
            );

        if (prefab == null)
        {
            WriteError(
                request.Client,
                500,
                "Unity could not create the prefab."
            );

            return;
        }

        AssetDatabase.SaveAssets();

        WriteOk(
            request.Client,
            $"Created prefab: {path}"
        );
    }

    private static void InstantiatePrefab(
        Request request
    )
    {
        PrefabInstantiateRequest data =
            JsonUtility.FromJson<PrefabInstantiateRequest>(
                request.Body
            );

        string path =
            NormalizeAssetPath(
                data.assetPath,
                false
            );

        GameObject prefab =
            AssetDatabase.LoadAssetAtPath<GameObject>(
                path
            );

        if (
            prefab == null
            ||
            PrefabUtility.GetPrefabAssetType(prefab)
            ==
            PrefabAssetType.NotAPrefab
        )
        {
            WriteError(
                request.Client,
                404,
                $"Prefab not found: {path}"
            );

            return;
        }

        GameObject parent =
            string.IsNullOrWhiteSpace(data.parentPath)
                ? null
                : Find(data.parentPath);

        if (
            !string.IsNullOrWhiteSpace(data.parentPath)
            &&
            parent == null
        )
        {
            WriteError(
                request.Client,
                404,
                $"Parent not found: {data.parentPath}"
            );

            return;
        }

        GameObject instance =
            PrefabUtility.InstantiatePrefab(
                prefab,
                SceneManager.GetActiveScene()
            )
            as GameObject;

        if (instance == null)
        {
            WriteError(
                request.Client,
                500,
                "Unity could not instantiate the prefab."
            );

            return;
        }

        if (parent != null)
        {
            Undo.SetTransformParent(
                instance.transform,
                parent.transform,
                "AI Set Prefab Parent"
            );
        }

        if (!string.IsNullOrWhiteSpace(data.name))
        {
            instance.name =
                data.name.Trim();
        }

        Undo.RegisterCreatedObjectUndo(
            instance,
            "AI Instantiate Prefab"
        );

        EditorSceneManager.MarkSceneDirty(
            instance.scene
        );

        WriteOk(
            request.Client,
            $"Instantiated prefab: {BuildPath(instance.transform)}"
        );
    }

    // ============================================
    // PATH HELPERS
    // ============================================

    private static string NormalizeAssetPath(
        string path,
        bool allowNewFile
    )
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException(
                "Asset path is required."
            );
        }

        string normalized =
            path
                .Replace('\\', '/')
                .Trim();

        if (
            !normalized.Equals(
                "Assets",
                StringComparison.Ordinal
            )
            &&
            !normalized.StartsWith(
                "Assets/",
                StringComparison.Ordinal
            )
        )
        {
            throw new UnauthorizedAccessException(
                "Asset path must be inside Assets/."
            );
        }

        if (
            normalized
                .Split('/')
                .Any(
                    part => part == ".."
                )
        )
        {
            throw new UnauthorizedAccessException(
                "Asset path cannot contain ..."
            );
        }

        if (
            !allowNewFile
            &&
            normalized.EndsWith(
                "/",
                StringComparison.Ordinal
            )
        )
        {
            normalized =
                normalized.TrimEnd('/');
        }

        return normalized;
    }

    private static void EnsureAssetDirectory(
        string assetPath
    )
    {
        string directory =
            Path
                .GetDirectoryName(assetPath)
                ?.Replace('\\', '/');

        if (
            string.IsNullOrWhiteSpace(directory)
            ||
            directory == "Assets"
        )
        {
            return;
        }

        string projectRoot =
            Directory
                .GetParent(Application.dataPath)
                .FullName;

        Directory.CreateDirectory(
            Path.Combine(
                projectRoot,
                directory
            )
        );

        AssetDatabase.Refresh();
    }

    // ============================================
    // GAMEOBJECT HELPERS
    // ============================================

    private static GameObject Find(
        string path
    )
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string[] parts =
            path
                .Trim('/')
                .Split('/');

        GameObject current =
            SceneManager
                .GetActiveScene()
                .GetRootGameObjects()
                .FirstOrDefault(
                    root =>
                        root.name.Equals(
                            parts[0],
                            StringComparison.Ordinal
                        )
                );

        if (current == null)
        {
            return null;
        }

        for (
            int i = 1;
            i < parts.Length;
            i++
        )
        {
            Transform child =
                Enumerable
                    .Range(
                        0,
                        current.transform.childCount
                    )
                    .Select(
                        index =>
                            current.transform.GetChild(index)
                    )
                    .FirstOrDefault(
                        item =>
                            item.name.Equals(
                                parts[i],
                                StringComparison.Ordinal
                            )
                    );

            if (child == null)
            {
                return null;
            }

            current =
                child.gameObject;
        }

        return current;
    }

    private static string BuildPath(
        Transform transform
    )
    {
        string path =
            transform.name;

        while (transform.parent != null)
        {
            transform =
                transform.parent;

            path =
                transform.name
                +
                "/"
                +
                path;
        }

        return path;
    }

    // ============================================
    // HTTP REQUEST
    // ============================================

    private static Request ReadRequest(
        TcpClient client
    )
    {
        client.ReceiveTimeout =
            5000;

        NetworkStream stream =
            client.GetStream();

        using StreamReader reader =
            new StreamReader(
                stream,
                Encoding.UTF8,
                false,
                1024,
                true
            );

        string line =
            reader.ReadLine();

        if (string.IsNullOrWhiteSpace(line))
        {
            throw new InvalidDataException(
                "Empty request."
            );
        }

        string[] first =
            line.Split(' ');

        if (first.Length < 2)
        {
            throw new InvalidDataException(
                "Invalid HTTP request line."
            );
        }

        int length =
            0;

        string header =
            "";

        while (
            !string.IsNullOrEmpty(
                line = reader.ReadLine()
            )
        )
        {
            int separator =
                line.IndexOf(':');

            if (separator <= 0)
            {
                continue;
            }

            string key =
                line
                    .Substring(
                        0,
                        separator
                    )
                    .Trim();

            string value =
                line
                    .Substring(
                        separator + 1
                    )
                    .Trim();

            if (
                key.Equals(
                    "Content-Length",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                int.TryParse(
                    value,
                    out length
                );
            }

            if (
                key.Equals(
                    "X-AI-Bridge",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                header =
                    value;
            }
        }

        char[] buffer =
            new char[length];

        int total =
            0;

        while (total < length)
        {
            int count =
                reader.Read(
                    buffer,
                    total,
                    length - total
                );

            if (count <= 0)
            {
                break;
            }

            total +=
                count;
        }

        return new Request(
            client,
            first[0],
            first[1].Split('?')[0],
            header,
            new string(
                buffer,
                0,
                total
            )
        );
    }

    // ============================================
    // HTTP RESPONSE
    // ============================================

    private static void WriteOk(
        TcpClient client,
        string message
    )
    {
        Response response =
            new Response
            {
                success = true,
                message = message,
                error = ""
            };

        Write(
            client,
            200,
            JsonUtility.ToJson(
                response,
                true
            )
        );
    }

    private static void WriteError(
        TcpClient client,
        int status,
        string message
    )
    {
        Response response =
            new Response
            {
                success = false,
                message = "",
                error = message
            };

        Write(
            client,
            status,
            JsonUtility.ToJson(
                response,
                true
            )
        );
    }

    private static void Write(
        TcpClient client,
        int status,
        string body
    )
    {
        byte[] data =
            Encoding.UTF8.GetBytes(
                body
            );

        string statusText =
            status == 200
                ? "OK"
                : status == 400
                    ? "Bad Request"
                    : status == 403
                        ? "Forbidden"
                        : status == 404
                            ? "Not Found"
                            : status == 405
                                ? "Method Not Allowed"
                                : "Internal Server Error";

        byte[] headers =
            Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status} {statusText}\r\n"
                +
                "Content-Type: application/json; charset=utf-8\r\n"
                +
                $"Content-Length: {data.Length}\r\n"
                +
                "Connection: close\r\n"
                +
                "\r\n"
            );

        using (client)
        using (
            NetworkStream stream =
                client.GetStream()
        )
        {
            stream.Write(
                headers,
                0,
                headers.Length
            );

            stream.Write(
                data,
                0,
                data.Length
            );
        }
    }

    // ============================================
    // REQUEST / RESPONSE TYPES
    // ============================================

    private sealed class Request
    {
        public TcpClient Client
        {
            get;
        }

        public string Method
        {
            get;
        }

        public string Path
        {
            get;
        }

        public string Header
        {
            get;
        }

        public string Body
        {
            get;
        }

        public Request(
            TcpClient client,
            string method,
            string path,
            string header,
            string body
        )
        {
            Client =
                client;

            Method =
                method;

            Path =
                path;

            Header =
                header;

            Body =
                body;
        }
    }

    [Serializable]
    private sealed class ComponentRequest
    {
        public string objectPath;
        public string componentType;
    }

    [Serializable]
    private sealed class PrimitiveRequest
    {
        public string primitiveType;
        public string name;
        public string parentPath;
    }

    [Serializable]
    private sealed class RenameRequest
    {
        public string objectPath;
        public string newName;
    }

    [Serializable]
    private sealed class ParentRequest
    {
        public string objectPath;
        public string parentPath;
    }

    [Serializable]
    private sealed class ActiveRequest
    {
        public string objectPath;
        public bool active;
    }

    [Serializable]
    private sealed class FindAssetsRequest
    {
        public string filter;
        public string searchFolder;
    }

    [Serializable]
    private sealed class AssetPathRequest
    {
        public string assetPath;
    }

    [Serializable]
    private sealed class CreateMaterialRequest
    {
        public string assetPath;
        public string shaderName;
    }

    [Serializable]
    private sealed class MaterialColorRequest
    {
        public string materialPath;
        public float red;
        public float green;
        public float blue;
        public float alpha;
    }

    [Serializable]
    private sealed class AssignMaterialRequest
    {
        public string objectPath;
        public string materialPath;
    }

    [Serializable]
    private sealed class VectorRequest
    {
        public string objectPath;
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    private sealed class DuplicateRequest
    {
        public string objectPath;
        public string newName;
        public string parentPath;
    }

    [Serializable]
    private sealed class RigidbodyRequest
    {
        public string objectPath;
        public float mass;
        public bool useGravity;
        public bool isKinematic;
    }

    [Serializable]
    private sealed class ColliderRequest
    {
        public string objectPath;
        public bool enabled;
        public bool isTrigger;
    }

    [Serializable]
    private sealed class PrefabCreateRequest
    {
        public string objectPath;
        public string assetPath;
    }

    [Serializable]
    private sealed class PrefabInstantiateRequest
    {
        public string assetPath;
        public string name;
        public string parentPath;
    }

    [Serializable]
    private sealed class AssetSearchResponse
    {
        public bool success;
        public int count;
        public string[] paths;
    }

    [Serializable]
    private sealed class AssetInfoResponse
    {
        public bool success;
        public string path;
        public string name;
        public string type;
    }

    [Serializable]
    private sealed class Response
    {
        public bool success;
        public string message;
        public string error;
    }
}