using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class UnityBridgeBatchServer
{
    private const string Prefix = "http://127.0.0.1:47824/";
    private const string RequiredHeader = "AI-Assistant-Local";

    private static readonly ConcurrentQueue<PendingBatch> pendingBatches =
        new ConcurrentQueue<PendingBatch>();

    private static readonly Dictionary<string, Type> componentTypeCache =
        new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

    private static HttpListener listener;
    private static Thread listenerThread;
    private static volatile bool running;

    static UnityBridgeBatchServer()
    {
        EditorApplication.update += ProcessPendingBatches;
        AssemblyReloadEvents.beforeAssemblyReload += Stop;
        EditorApplication.quitting += Stop;
        EditorApplication.delayCall += Start;
    }

    private static void Start()
    {
        if (running)
        {
            return;
        }

        try
        {
            listener = new HttpListener();
            listener.Prefixes.Add(Prefix);
            listener.Start();

            running = true;
            listenerThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "AI Assistant Unity Batch Bridge"
            };
            listenerThread.Start();

            Debug.Log("[AI Batch Bridge] Listening on 127.0.0.1:47824 (transactional)");
        }
        catch (Exception ex)
        {
            running = false;
            Debug.LogError("[AI Batch Bridge] Start failed: " + ex.Message);
        }
    }

    private static void Stop()
    {
        running = false;

        try
        {
            listener?.Stop();
            listener?.Close();
        }
        catch
        {
        }
    }

    private static void ListenLoop()
    {
        while (running)
        {
            try
            {
                HttpListenerContext context = listener.GetContext();
                HandleRequest(context);
            }
            catch (Exception ex)
            {
                if (running)
                {
                    Debug.LogError("[AI Batch Bridge] Listener error: " + ex.Message);
                }
            }
        }
    }

    private static void HandleRequest(HttpListenerContext context)
    {
        try
        {
            if (context.Request.Headers["X-AI-Bridge"] != RequiredHeader)
            {
                WriteResponse(context, 403, JsonUtility.ToJson(new BasicResponse
                {
                    success = false,
                    message = "Unauthorized bridge request."
                }));
                return;
            }

            if (context.Request.HttpMethod != "POST")
            {
                WriteResponse(context, 405, JsonUtility.ToJson(new BasicResponse
                {
                    success = false,
                    message = "POST required."
                }));
                return;
            }

            string path = context.Request.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant();
            if (path != "/execute-batch")
            {
                WriteResponse(context, 404, JsonUtility.ToJson(new BasicResponse
                {
                    success = false,
                    message = "Unknown batch endpoint."
                }));
                return;
            }

            string body;
            using (StreamReader reader = new StreamReader(
                context.Request.InputStream,
                context.Request.ContentEncoding))
            {
                body = reader.ReadToEnd();
            }

            BatchRequest request = JsonUtility.FromJson<BatchRequest>(body);
            if (request == null || request.operations == null)
            {
                WriteResponse(context, 400, JsonUtility.ToJson(new BasicResponse
                {
                    success = false,
                    message = "Invalid batch request."
                }));
                return;
            }

            pendingBatches.Enqueue(new PendingBatch(context, request));
        }
        catch (Exception ex)
        {
            WriteResponse(context, 500, JsonUtility.ToJson(new BasicResponse
            {
                success = false,
                message = ex.GetType().Name + ": " + ex.Message
            }));
        }
    }

    private static void ProcessPendingBatches()
    {
        while (pendingBatches.TryDequeue(out PendingBatch pending))
        {
            ExecuteBatch(pending.context, pending.request);
        }
    }

    private static void ExecuteBatch(
        HttpListenerContext context,
        BatchRequest request)
    {
        BatchResponse response = new BatchResponse
        {
            success = true,
            rolledBack = false,
            results = new List<OperationResult>()
        };

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("AI Assistant transactional batch");

        bool transactionClosed = false;

        try
        {
            for (int i = 0; i < request.operations.Length; i++)
            {
                OperationResult result = ExecuteOperation(i, request.operations[i]);
                response.results.Add(result);

                if (!result.success)
                {
                    response.success = false;

                    if (request.stopOnFailure)
                    {
                        break;
                    }
                }
            }

            if (!response.success)
            {
                RollbackBatch(undoGroup);
                transactionClosed = true;
                response.rolledBack = true;
            }
            else
            {
                if (request.saveScene)
                {
                    SaveActiveScene();
                }

                Undo.CollapseUndoOperations(undoGroup);
                transactionClosed = true;
            }

            WriteResponse(
                context,
                response.success ? 200 : 400,
                JsonUtility.ToJson(response)
            );
        }
        catch (Exception ex)
        {
            if (!transactionClosed)
            {
                try
                {
                    RollbackBatch(undoGroup);
                }
                catch
                {
                }
            }

            WriteResponse(context, 500, JsonUtility.ToJson(new BasicResponse
            {
                success = false,
                message = ex.GetType().Name + ": " + ex.Message
            }));
        }
    }

    private static void RollbackBatch(int undoGroup)
    {
        Undo.RevertAllDownToGroup(undoGroup);
        SceneView.RepaintAll();
    }

    private static void SaveActiveScene()
    {
        Scene scene = SceneManager.GetActiveScene();

        if (!scene.IsValid())
        {
            throw new InvalidOperationException("Active Unity scene is not valid.");
        }

        if (string.IsNullOrWhiteSpace(scene.path))
        {
            throw new InvalidOperationException(
                "Active scene has no path. Save it manually once first."
            );
        }

        if (!EditorSceneManager.SaveScene(scene))
        {
            throw new InvalidOperationException("Unity could not save the active scene.");
        }
    }

    private static OperationResult ExecuteOperation(
        int index,
        BatchOperation operation)
    {
        OperationResult result = new OperationResult
        {
            index = index,
            operation = operation?.operation ?? ""
        };

        try
        {
            if (operation == null)
            {
                throw new InvalidOperationException("Batch operation is null.");
            }

            switch (operation.operation)
            {
                case "create_gameobject":
                    result.message = CreateGameObject(operation);
                    break;
                case "create_primitive":
                    result.message = CreatePrimitive(operation);
                    break;
                case "delete_gameobject":
                    result.message = DeleteGameObject(operation);
                    break;
                case "rename_gameobject":
                    result.message = RenameGameObject(operation);
                    break;
                case "set_parent":
                    result.message = SetParent(operation);
                    break;
                case "set_active":
                    result.message = SetActive(operation);
                    break;
                case "set_position":
                    result.message = SetPosition(operation);
                    break;
                case "set_rotation":
                    result.message = SetRotation(operation);
                    break;
                case "set_scale":
                    result.message = SetScale(operation);
                    break;
                case "add_component":
                    result.message = AddComponent(operation);
                    break;
                case "remove_component":
                    result.message = RemoveComponent(operation);
                    break;
                case "set_component_property":
                    result.message = SetComponentProperty(operation);
                    break;
                case "create_script":
                    result.message = CreateScript(operation);
                    break;
                default:
                    throw new InvalidOperationException(
                        "Unknown batch operation: " + operation.operation
                    );
            }

            result.success = true;
        }
        catch (Exception ex)
        {
            result.success = false;
            result.message = ex.GetType().Name + ": " + ex.Message;
        }

        return result;
    }

    private static string CreateGameObject(BatchOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.name))
        {
            throw new InvalidOperationException("name is required.");
        }

        string desiredPath = BuildPath(operation.parentPath, operation.name);
        GameObject existing = TryFindGameObject(desiredPath);
        if (existing != null)
        {
            return "Reused existing " + desiredPath;
        }

        GameObject gameObject = new GameObject(operation.name);
        Undo.RegisterCreatedObjectUndo(gameObject, "AI Create GameObject");

        if (!string.IsNullOrWhiteSpace(operation.parentPath))
        {
            GameObject parent = FindGameObject(operation.parentPath);
            Undo.SetTransformParent(
                gameObject.transform,
                parent.transform,
                "AI Parent Created GameObject"
            );
        }

        EditorUtility.SetDirty(gameObject);
        return "Created " + GetHierarchyPath(gameObject.transform);
    }

    private static string CreatePrimitive(BatchOperation operation)
    {
        if (!Enum.TryParse(operation.primitiveType, true, out PrimitiveType primitiveType))
        {
            throw new InvalidOperationException(
                "Invalid primitiveType: " + operation.primitiveType
            );
        }

        string objectName = string.IsNullOrWhiteSpace(operation.name)
            ? primitiveType.ToString()
            : operation.name;

        string desiredPath = BuildPath(operation.parentPath, objectName);
        GameObject existing = TryFindGameObject(desiredPath);
        if (existing != null)
        {
            return "Reused existing " + desiredPath;
        }

        GameObject gameObject = GameObject.CreatePrimitive(primitiveType);
        gameObject.name = objectName;
        Undo.RegisterCreatedObjectUndo(gameObject, "AI Create Primitive");

        if (!string.IsNullOrWhiteSpace(operation.parentPath))
        {
            GameObject parent = FindGameObject(operation.parentPath);
            Undo.SetTransformParent(
                gameObject.transform,
                parent.transform,
                "AI Parent Created Primitive"
            );
        }

        EditorUtility.SetDirty(gameObject);
        return "Created " + GetHierarchyPath(gameObject.transform);
    }

    private static string DeleteGameObject(BatchOperation operation)
    {
        GameObject gameObject = FindGameObject(operation.objectPath);
        string path = GetHierarchyPath(gameObject.transform);
        Undo.DestroyObjectImmediate(gameObject);
        return "Deleted " + path;
    }

    private static string RenameGameObject(BatchOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.newName))
        {
            throw new InvalidOperationException("newName is required.");
        }

        GameObject gameObject = FindGameObject(operation.objectPath);

        if (string.Equals(gameObject.name, operation.newName, StringComparison.Ordinal))
        {
            return "Already named " + operation.newName;
        }

        Undo.RecordObject(gameObject, "AI Rename GameObject");
        gameObject.name = operation.newName;
        EditorUtility.SetDirty(gameObject);
        return "Renamed to " + GetHierarchyPath(gameObject.transform);
    }

    private static string SetParent(BatchOperation operation)
    {
        GameObject gameObject = FindGameObject(operation.objectPath);
        Transform parent = string.IsNullOrWhiteSpace(operation.parentPath)
            ? null
            : FindGameObject(operation.parentPath).transform;

        if (gameObject.transform.parent == parent)
        {
            return "Parent already satisfied.";
        }

        Undo.SetTransformParent(gameObject.transform, parent, "AI Set Parent");
        return "Parent set: " + GetHierarchyPath(gameObject.transform);
    }

    private static string SetActive(BatchOperation operation)
    {
        GameObject gameObject = FindGameObject(operation.objectPath);
        if (gameObject.activeSelf == operation.boolValue)
        {
            return "Active already " + operation.boolValue;
        }

        Undo.RecordObject(gameObject, "AI Set Active");
        gameObject.SetActive(operation.boolValue);
        EditorUtility.SetDirty(gameObject);
        return "Active=" + operation.boolValue;
    }

    private static string SetPosition(BatchOperation operation)
    {
        GameObject gameObject = FindGameObject(operation.objectPath);
        Vector3 value = new Vector3(operation.x, operation.y, operation.z);

        if (gameObject.transform.position == value)
        {
            return "Position already satisfied.";
        }

        Undo.RecordObject(gameObject.transform, "AI Set Position");
        gameObject.transform.position = value;
        EditorUtility.SetDirty(gameObject.transform);
        return "Position set.";
    }

    private static string SetRotation(BatchOperation operation)
    {
        GameObject gameObject = FindGameObject(operation.objectPath);
        Vector3 value = new Vector3(operation.x, operation.y, operation.z);

        Undo.RecordObject(gameObject.transform, "AI Set Rotation");
        gameObject.transform.eulerAngles = value;
        EditorUtility.SetDirty(gameObject.transform);
        return "Rotation set.";
    }

    private static string SetScale(BatchOperation operation)
    {
        GameObject gameObject = FindGameObject(operation.objectPath);
        Vector3 value = new Vector3(operation.x, operation.y, operation.z);

        if (gameObject.transform.localScale == value)
        {
            return "Scale already satisfied.";
        }

        Undo.RecordObject(gameObject.transform, "AI Set Scale");
        gameObject.transform.localScale = value;
        EditorUtility.SetDirty(gameObject.transform);
        return "Scale set.";
    }

    private static string AddComponent(BatchOperation operation)
    {
        GameObject gameObject = FindGameObject(operation.objectPath);
        Type componentType = FindType(operation.componentType);

        if (componentType == null)
        {
            throw new InvalidOperationException(
                "Component type not found: " + operation.componentType
            );
        }

        if (!typeof(Component).IsAssignableFrom(componentType))
        {
            throw new InvalidOperationException(
                "Type is not a Unity Component: " + operation.componentType
            );
        }

        Component existing = gameObject.GetComponent(componentType);
        if (existing != null)
        {
            return "Component already exists: " + componentType.FullName;
        }

        Undo.AddComponent(gameObject, componentType);
        return "Added component " + componentType.FullName;
    }

    private static string RemoveComponent(BatchOperation operation)
    {
        GameObject gameObject = FindGameObject(operation.objectPath);
        Type componentType = FindType(operation.componentType);

        if (componentType == null)
        {
            throw new InvalidOperationException(
                "Component type not found: " + operation.componentType
            );
        }

        Component component = gameObject.GetComponent(componentType);
        if (component == null)
        {
            return "Component not present.";
        }

        if (component is Transform)
        {
            throw new InvalidOperationException("Transform cannot be removed.");
        }

        Undo.DestroyObjectImmediate(component);
        return "Removed component " + componentType.FullName;
    }

    private static string SetComponentProperty(BatchOperation operation)
    {
        GameObject gameObject = FindGameObject(operation.objectPath);
        Type componentType = FindType(operation.componentType);

        if (componentType == null)
        {
            throw new InvalidOperationException(
                "Component type not found: " + operation.componentType
            );
        }

        Component component = gameObject.GetComponent(componentType);
        if (component == null)
        {
            throw new InvalidOperationException(
                "Component is not attached: " + operation.componentType
            );
        }

        SerializedObject serializedObject = new SerializedObject(component);
        SerializedProperty property = FindSerializedProperty(
            serializedObject,
            operation.propertyName
        );

        if (property == null)
        {
            throw new InvalidOperationException(
                "Serialized property not found: " + operation.propertyName
            );
        }

        Undo.RecordObject(component, "AI Set Component Property");

        switch (operation.valueType)
        {
            case "int":
                property.intValue = operation.intValue;
                break;
            case "float":
                property.floatValue = operation.floatValue;
                break;
            case "bool":
                property.boolValue = operation.boolValue;
                break;
            case "string":
                property.stringValue = operation.stringValue ?? "";
                break;
            case "vector2":
                property.vector2Value = new Vector2(operation.x, operation.y);
                break;
            case "vector3":
                property.vector3Value = new Vector3(
                    operation.x,
                    operation.y,
                    operation.z
                );
                break;
            case "color":
                property.colorValue = new Color(
                    operation.r,
                    operation.g,
                    operation.b,
                    operation.a
                );
                break;
            default:
                throw new InvalidOperationException(
                    "Unsupported valueType: " + operation.valueType
                );
        }

        serializedObject.ApplyModifiedProperties();
        EditorUtility.SetDirty(component);
        return "Set " + operation.componentType + "." + operation.propertyName;
    }

    private static SerializedProperty FindSerializedProperty(
        SerializedObject serializedObject,
        string requestedName)
    {
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            return null;
        }

        SerializedProperty exact = serializedObject.FindProperty(requestedName);
        if (exact != null)
        {
            return exact;
        }

        string normalizedRequested = NormalizeSerializedPropertyName(requestedName);
        SerializedProperty iterator = serializedObject.GetIterator();
        bool enterChildren = true;

        while (iterator.NextVisible(enterChildren))
        {
            enterChildren = false;
            if (iterator.depth != 0)
            {
                continue;
            }

            if (
                string.Equals(
                    normalizedRequested,
                    NormalizeSerializedPropertyName(iterator.name),
                    StringComparison.OrdinalIgnoreCase
                )
                || string.Equals(
                    normalizedRequested,
                    NormalizeSerializedPropertyName(iterator.displayName),
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return iterator.Copy();
            }
        }

        return null;
    }

    private static string NormalizeSerializedPropertyName(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return "";
        }

        if (propertyName.StartsWith("m_", StringComparison.OrdinalIgnoreCase))
        {
            propertyName = propertyName.Substring(2);
        }

        StringBuilder builder = new StringBuilder(propertyName.Length);
        foreach (char c in propertyName)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static string CreateScript(BatchOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.assetPath))
        {
            throw new InvalidOperationException("assetPath is required.");
        }

        if (!operation.assetPath.StartsWith("Assets/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Script assetPath must be inside Assets/."
            );
        }

        if (!operation.assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Script assetPath must end in .cs."
            );
        }

        string assetsRoot = Path.GetFullPath(Application.dataPath);
        string projectRoot = Directory.GetParent(assetsRoot).FullName;
        string fullPath = Path.GetFullPath(
            Path.Combine(projectRoot, operation.assetPath)
        );

        if (!fullPath.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Script path escaped Assets directory."
            );
        }

        string directory = Path.GetDirectoryName(fullPath);
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            fullPath,
            operation.content ?? "",
            new UTF8Encoding(false)
        );

        AssetDatabase.ImportAsset(
            operation.assetPath,
            ImportAssetOptions.ForceUpdate
        );

        return "Created script " + operation.assetPath;
    }

    private static string BuildPath(string parentPath, string name)
    {
        return string.IsNullOrWhiteSpace(parentPath)
            ? name
            : parentPath.TrimEnd('/') + "/" + name;
    }

    private static GameObject FindGameObject(string objectPath)
    {
        GameObject result = TryFindGameObject(objectPath);
        if (result == null)
        {
            throw new InvalidOperationException(
                "GameObject not found: " + objectPath
            );
        }

        return result;
    }

    private static GameObject TryFindGameObject(string objectPath)
    {
        if (string.IsNullOrWhiteSpace(objectPath))
        {
            return null;
        }

        string[] parts = objectPath
            .Replace('\\', '/')
            .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            return null;
        }

        GameObject current = null;
        foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            if (root.name == parts[0])
            {
                current = root;
                break;
            }
        }

        if (current == null)
        {
            return null;
        }

        for (int i = 1; i < parts.Length; i++)
        {
            Transform child = current.transform.Find(parts[i]);
            if (child == null)
            {
                return null;
            }

            current = child.gameObject;
        }

        return current;
    }

    private static Type FindType(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        if (componentTypeCache.TryGetValue(typeName, out Type cached))
        {
            return cached;
        }

        foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type exact = assembly.GetType(
                typeName,
                throwOnError: false,
                ignoreCase: true
            );

            if (exact != null && typeof(Component).IsAssignableFrom(exact))
            {
                componentTypeCache[typeName] = exact;
                return exact;
            }
        }

        foreach (Type candidate in TypeCache.GetTypesDerivedFrom<Component>())
        {
            if (
                candidate.Name.Equals(typeName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    candidate.FullName,
                    typeName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                componentTypeCache[typeName] = candidate;
                return candidate;
            }
        }

        return null;
    }

    private static string GetHierarchyPath(Transform transform)
    {
        string path = transform.name;
        Transform parent = transform.parent;

        while (parent != null)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }

        return path;
    }

    private static void WriteResponse(
        HttpListenerContext context,
        int statusCode,
        string json)
    {
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }
        catch
        {
        }
    }

    [Serializable]
    private class BatchRequest
    {
        public BatchOperation[] operations;
        public bool stopOnFailure = true;
        public bool saveScene = false;
    }

    [Serializable]
    private class BatchOperation
    {
        public string operation;
        public string objectPath;
        public string parentPath;
        public string name;
        public string newName;
        public string primitiveType;
        public string componentType;
        public string propertyName;
        public string valueType;
        public int intValue;
        public float floatValue;
        public bool boolValue;
        public string stringValue;
        public float x;
        public float y;
        public float z;
        public float r;
        public float g;
        public float b;
        public float a = 1f;
        public string assetPath;
        public string content;
    }

    [Serializable]
    private class BatchResponse
    {
        public bool success;
        public bool rolledBack;
        public List<OperationResult> results;
    }

    [Serializable]
    private class OperationResult
    {
        public int index;
        public string operation;
        public bool success;
        public string message;
    }

    [Serializable]
    private class BasicResponse
    {
        public bool success;
        public string message;
    }

    private sealed class PendingBatch
    {
        public HttpListenerContext context;
        public BatchRequest request;

        public PendingBatch(
            HttpListenerContext context,
            BatchRequest request)
        {
            this.context = context;
            this.request = request;
        }
    }
}
