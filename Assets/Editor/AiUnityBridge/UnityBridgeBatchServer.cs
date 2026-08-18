using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

using UnityEditor;
using UnityEditor.SceneManagement;

using UnityEngine;


[InitializeOnLoad]
public static class UnityBridgeBatchServer
{
    private const string Prefix =
        "http://127.0.0.1:47824/";


    private const string RequiredHeader =
        "AI-Assistant-Local";


    private static HttpListener listener;

    private static Thread listenerThread;

    private static bool running;


    // ============================================================
    // START
    // ============================================================

    static UnityBridgeBatchServer()
    {
        EditorApplication.delayCall +=
            Start;
    }


    private static void Start()
    {
        if (running)
        {
            return;
        }


        try
        {
            listener =
                new HttpListener();


            listener.Prefixes.Add(
                Prefix
            );


            listener.Start();


            running =
                true;


            listenerThread =
                new Thread(
                    ListenLoop
                );


            listenerThread.IsBackground =
                true;


            listenerThread.Start();


            Debug.Log(
                "[AI Batch Bridge] Listening on 127.0.0.1:47824"
            );
        }
        catch (Exception ex)
        {
            Debug.LogError(
                "[AI Batch Bridge] Start failed: "
                +
                ex.Message
            );
        }
    }


    // ============================================================
    // LISTENER
    // ============================================================

    private static void ListenLoop()
    {
        while (running)
        {
            try
            {
                HttpListenerContext context =
                    listener.GetContext();


                HandleRequest(
                    context
                );
            }
            catch (Exception ex)
            {
                if (running)
                {
                    Debug.LogError(
                        "[AI Batch Bridge] Listener error: "
                        +
                        ex.Message
                    );
                }
            }
        }
    }


    // ============================================================
    // REQUEST
    // ============================================================

    private static void HandleRequest(
        HttpListenerContext context
    )
    {
        try
        {
            string bridgeHeader =
                context.Request.Headers[
                    "X-AI-Bridge"
                ];


            if (
                bridgeHeader !=
                RequiredHeader
            )
            {
                WriteResponse(
                    context,
                    403,
                    JsonUtility.ToJson(
                        new BasicResponse
                        {
                            success =
                                false,

                            message =
                                "Unauthorized bridge request."
                        }
                    )
                );


                return;
            }


            if (
                context.Request.HttpMethod !=
                "POST"
            )
            {
                WriteResponse(
                    context,
                    405,
                    JsonUtility.ToJson(
                        new BasicResponse
                        {
                            success =
                                false,

                            message =
                                "POST required."
                        }
                    )
                );


                return;
            }


            string path =
                context.Request.Url
                    .AbsolutePath
                    .TrimEnd('/')
                    .ToLowerInvariant();


            if (
                path !=
                "/execute-batch"
            )
            {
                WriteResponse(
                    context,
                    404,
                    JsonUtility.ToJson(
                        new BasicResponse
                        {
                            success =
                                false,

                            message =
                                "Unknown batch endpoint."
                        }
                    )
                );


                return;
            }


            string body;


            using (
                StreamReader reader =
                    new StreamReader(
                        context.Request.InputStream,
                        context.Request.ContentEncoding
                    )
            )
            {
                body =
                    reader.ReadToEnd();
            }


            BatchRequest request =
                JsonUtility.FromJson<BatchRequest>(
                    body
                );


            if (
                request == null
                ||
                request.operations == null
            )
            {
                WriteResponse(
                    context,
                    400,
                    JsonUtility.ToJson(
                        new BasicResponse
                        {
                            success =
                                false,

                            message =
                                "Invalid batch request."
                        }
                    )
                );


                return;
            }


            // All Unity API calls must happen on main thread.
            EditorApplication.delayCall +=
                () =>
                {
                    ExecuteBatch(
                        context,
                        request
                    );
                };
        }
        catch (Exception ex)
        {
            WriteResponse(
                context,
                500,
                JsonUtility.ToJson(
                    new BasicResponse
                    {
                        success =
                            false,

                        message =
                            ex.Message
                    }
                )
            );
        }
    }


    // ============================================================
    // EXECUTE BATCH
    // ============================================================

    private static void ExecuteBatch(
        HttpListenerContext context,
        BatchRequest request
    )
    {
        BatchResponse response =
            new BatchResponse();


        response.success =
            true;


        response.results =
            new List<OperationResult>();


        try
        {
            for (
                int i = 0;
                i < request.operations.Length;
                i++
            )
            {
                BatchOperation operation =
                    request.operations[i];


                OperationResult result =
                    ExecuteOperation(
                        i,
                        operation
                    );


                response.results.Add(
                    result
                );


                if (
                    !result.success
                )
                {
                    response.success =
                        false;


                    // Default behavior:
                    // stop dependent work after first failure.
                    if (
                        request.stopOnFailure
                    )
                    {
                        break;
                    }
                }
            }


            if (
                request.saveScene
            )
            {
                EditorSceneManager.SaveOpenScenes();
            }


            WriteResponse(
                context,
                response.success
                    ?
                    200
                    :
                    400,

                JsonUtility.ToJson(
                    response
                )
            );
        }
        catch (Exception ex)
        {
            WriteResponse(
                context,
                500,
                JsonUtility.ToJson(
                    new BasicResponse
                    {
                        success =
                            false,

                        message =
                            ex.Message
                    }
                )
            );
        }
    }


    // ============================================================
    // EXECUTE ONE OPERATION
    // ============================================================

    private static OperationResult ExecuteOperation(
        int index,
        BatchOperation operation
    )
    {
        OperationResult result =
            new OperationResult();


        result.index =
            index;


        result.operation =
            operation.operation;


        try
        {
            switch (
                operation.operation
            )
            {
                case "create_gameobject":
                    result.message =
                        CreateGameObject(
                            operation
                        );

                    break;


                case "create_primitive":
                    result.message =
                        CreatePrimitive(
                            operation
                        );

                    break;


                case "delete_gameobject":
                    result.message =
                        DeleteGameObject(
                            operation
                        );

                    break;


                case "rename_gameobject":
                    result.message =
                        RenameGameObject(
                            operation
                        );

                    break;


                case "set_parent":
                    result.message =
                        SetParent(
                            operation
                        );

                    break;


                case "set_active":
                    result.message =
                        SetActive(
                            operation
                        );

                    break;


                case "set_position":
                    result.message =
                        SetPosition(
                            operation
                        );

                    break;


                case "set_rotation":
                    result.message =
                        SetRotation(
                            operation
                        );

                    break;


                case "set_scale":
                    result.message =
                        SetScale(
                            operation
                        );

                    break;


                case "add_component":
                    result.message =
                        AddComponent(
                            operation
                        );

                    break;


                case "remove_component":
                    result.message =
                        RemoveComponent(
                            operation
                        );

                    break;


                case "set_component_property":
                    result.message =
                        SetComponentProperty(
                            operation
                        );

                    break;


                case "create_script":
                    result.message =
                        CreateScript(
                            operation
                        );

                    break;


                default:
                    throw new InvalidOperationException(
                        "Unknown batch operation: "
                        +
                        operation.operation
                    );
            }


            result.success =
                true;
        }
        catch (Exception ex)
        {
            result.success =
                false;


            result.message =
                ex.Message;
        }


        return
            result;
    }


    // ============================================================
    // CREATE GAMEOBJECT
    // ============================================================

    private static string CreateGameObject(
        BatchOperation operation
    )
    {
        if (
            string.IsNullOrWhiteSpace(
                operation.name
            )
        )
        {
            throw new InvalidOperationException(
                "name is required."
            );
        }


        GameObject gameObject =
            new GameObject(
                operation.name
            );


        Undo.RegisterCreatedObjectUndo(
            gameObject,
            "AI Create GameObject"
        );


        if (
            !string.IsNullOrWhiteSpace(
                operation.parentPath
            )
        )
        {
            GameObject parent =
                FindGameObject(
                    operation.parentPath
                );


            gameObject.transform.SetParent(
                parent.transform,
                true
            );
        }


        EditorUtility.SetDirty(
            gameObject
        );


        return
            "Created "
            +
            GetHierarchyPath(
                gameObject.transform
            );
    }


    // ============================================================
    // CREATE PRIMITIVE
    // ============================================================

    private static string CreatePrimitive(
        BatchOperation operation
    )
    {
        PrimitiveType primitiveType;


        if (
            !Enum.TryParse(
                operation.primitiveType,
                true,
                out primitiveType
            )
        )
        {
            throw new InvalidOperationException(
                "Invalid primitiveType: "
                +
                operation.primitiveType
            );
        }


        GameObject gameObject =
            GameObject.CreatePrimitive(
                primitiveType
            );


        Undo.RegisterCreatedObjectUndo(
            gameObject,
            "AI Create Primitive"
        );


        if (
            !string.IsNullOrWhiteSpace(
                operation.name
            )
        )
        {
            gameObject.name =
                operation.name;
        }


        if (
            !string.IsNullOrWhiteSpace(
                operation.parentPath
            )
        )
        {
            GameObject parent =
                FindGameObject(
                    operation.parentPath
                );


            gameObject.transform.SetParent(
                parent.transform,
                true
            );
        }


        EditorUtility.SetDirty(
            gameObject
        );


        return
            "Created "
            +
            GetHierarchyPath(
                gameObject.transform
            );
    }


    // ============================================================
    // DELETE GAMEOBJECT
    // ============================================================

    private static string DeleteGameObject(
        BatchOperation operation
    )
    {
        GameObject gameObject =
            FindGameObject(
                operation.objectPath
            );


        string path =
            GetHierarchyPath(
                gameObject.transform
            );


        Undo.DestroyObjectImmediate(
            gameObject
        );


        return
            "Deleted "
            +
            path;
    }


    // ============================================================
    // RENAME
    // ============================================================

    private static string RenameGameObject(
        BatchOperation operation
    )
    {
        GameObject gameObject =
            FindGameObject(
                operation.objectPath
            );


        if (
            string.IsNullOrWhiteSpace(
                operation.newName
            )
        )
        {
            throw new InvalidOperationException(
                "newName is required."
            );
        }


        Undo.RecordObject(
            gameObject,
            "AI Rename GameObject"
        );


        gameObject.name =
            operation.newName;


        EditorUtility.SetDirty(
            gameObject
        );


        return
            "Renamed to "
            +
            GetHierarchyPath(
                gameObject.transform
            );
    }


    // ============================================================
    // PARENT
    // ============================================================

    private static string SetParent(
        BatchOperation operation
    )
    {
        GameObject gameObject =
            FindGameObject(
                operation.objectPath
            );


        Transform parent =
            null;


        if (
            !string.IsNullOrWhiteSpace(
                operation.parentPath
            )
        )
        {
            parent =
                FindGameObject(
                    operation.parentPath
                )
                .transform;
        }


        Undo.SetTransformParent(
            gameObject.transform,
            parent,
            "AI Set Parent"
        );


        return
            "Parent set: "
            +
            GetHierarchyPath(
                gameObject.transform
            );
    }


    // ============================================================
    // ACTIVE
    // ============================================================

    private static string SetActive(
        BatchOperation operation
    )
    {
        GameObject gameObject =
            FindGameObject(
                operation.objectPath
            );


        Undo.RecordObject(
            gameObject,
            "AI Set Active"
        );


        gameObject.SetActive(
            operation.boolValue
        );


        EditorUtility.SetDirty(
            gameObject
        );


        return
            "Active="
            +
            operation.boolValue;
    }


    // ============================================================
    // POSITION
    // ============================================================

    private static string SetPosition(
        BatchOperation operation
    )
    {
        GameObject gameObject =
            FindGameObject(
                operation.objectPath
            );


        Undo.RecordObject(
            gameObject.transform,
            "AI Set Position"
        );


        gameObject.transform.position =
            new Vector3(
                operation.x,
                operation.y,
                operation.z
            );


        EditorUtility.SetDirty(
            gameObject.transform
        );


        return
            "Position set.";
    }


    // ============================================================
    // ROTATION
    // ============================================================

    private static string SetRotation(
        BatchOperation operation
    )
    {
        GameObject gameObject =
            FindGameObject(
                operation.objectPath
            );


        Undo.RecordObject(
            gameObject.transform,
            "AI Set Rotation"
        );


        gameObject.transform.eulerAngles =
            new Vector3(
                operation.x,
                operation.y,
                operation.z
            );


        EditorUtility.SetDirty(
            gameObject.transform
        );


        return
            "Rotation set.";
    }


    // ============================================================
    // SCALE
    // ============================================================

    private static string SetScale(
        BatchOperation operation
    )
    {
        GameObject gameObject =
            FindGameObject(
                operation.objectPath
            );


        Undo.RecordObject(
            gameObject.transform,
            "AI Set Scale"
        );


        gameObject.transform.localScale =
            new Vector3(
                operation.x,
                operation.y,
                operation.z
            );


        EditorUtility.SetDirty(
            gameObject.transform
        );


        return
            "Scale set.";
    }


    // ============================================================
    // ADD COMPONENT
    // ============================================================

    private static string AddComponent(
        BatchOperation operation
    )
    {
        GameObject gameObject =
            FindGameObject(
                operation.objectPath
            );


        Type componentType =
            FindType(
                operation.componentType
            );


        if (
            componentType ==
            null
        )
        {
            throw new InvalidOperationException(
                "Component type not found: "
                +
                operation.componentType
            );
        }


        if (
            !typeof(Component)
                .IsAssignableFrom(
                    componentType
                )
        )
        {
            throw new InvalidOperationException(
                "Type is not a Unity Component: "
                +
                operation.componentType
            );
        }


        Component existing =
            gameObject.GetComponent(
                componentType
            );


        if (
            existing != null
        )
        {
            return
                "Component already exists: "
                +
                componentType.FullName;
        }


        Undo.AddComponent(
            gameObject,
            componentType
        );


        return
            "Added component "
            +
            componentType.FullName;
    }


    // ============================================================
    // REMOVE COMPONENT
    // ============================================================

    private static string RemoveComponent(
        BatchOperation operation
    )
    {
        GameObject gameObject =
            FindGameObject(
                operation.objectPath
            );


        Type componentType =
            FindType(
                operation.componentType
            );


        if (
            componentType ==
            null
        )
        {
            throw new InvalidOperationException(
                "Component type not found: "
                +
                operation.componentType
            );
        }


        Component component =
            gameObject.GetComponent(
                componentType
            );


        if (
            component ==
            null
        )
        {
            return
                "Component not present.";
        }


        if (
            component is Transform
        )
        {
            throw new InvalidOperationException(
                "Transform cannot be removed."
            );
        }


        Undo.DestroyObjectImmediate(
            component
        );


        return
            "Removed component "
            +
            componentType.FullName;
    }


    // ============================================================
    // SET SERIALIZED COMPONENT PROPERTY
    // ============================================================

    private static string SetComponentProperty(
        BatchOperation operation
    )
    {
        GameObject gameObject =
            FindGameObject(
                operation.objectPath
            );


        Type componentType =
            FindType(
                operation.componentType
            );


        if (
            componentType ==
            null
        )
        {
            throw new InvalidOperationException(
                "Component type not found: "
                +
                operation.componentType
            );
        }


        Component component =
            gameObject.GetComponent(
                componentType
            );


        if (
            component ==
            null
        )
        {
            throw new InvalidOperationException(
                "Component is not attached: "
                +
                operation.componentType
            );
        }


        SerializedObject serializedObject =
            new SerializedObject(
                component
            );


        SerializedProperty property =
            serializedObject.FindProperty(
                operation.propertyName
            );


        if (
            property ==
            null
        )
        {
            throw new InvalidOperationException(
                "Serialized property not found: "
                +
                operation.propertyName
            );
        }


        Undo.RecordObject(
            component,
            "AI Set Component Property"
        );


        switch (
            operation.valueType
        )
        {
            case "int":
                property.intValue =
                    operation.intValue;

                break;


            case "float":
                property.floatValue =
                    operation.floatValue;

                break;


            case "bool":
                property.boolValue =
                    operation.boolValue;

                break;


            case "string":
                property.stringValue =
                    operation.stringValue
                    ??
                    "";

                break;


            case "vector2":
                property.vector2Value =
                    new Vector2(
                        operation.x,
                        operation.y
                    );

                break;


            case "vector3":
                property.vector3Value =
                    new Vector3(
                        operation.x,
                        operation.y,
                        operation.z
                    );

                break;


            case "color":
                property.colorValue =
                    new Color(
                        operation.r,
                        operation.g,
                        operation.b,
                        operation.a
                    );

                break;


            default:
                throw new InvalidOperationException(
                    "Unsupported valueType: "
                    +
                    operation.valueType
                );
        }


        serializedObject.ApplyModifiedProperties();


        EditorUtility.SetDirty(
            component
        );


        return
            "Set "
            +
            operation.componentType
            +
            "."
            +
            operation.propertyName;
    }


    // ============================================================
    // CREATE SCRIPT
    // ============================================================

    private static string CreateScript(
        BatchOperation operation
    )
    {
        if (
            string.IsNullOrWhiteSpace(
                operation.assetPath
            )
        )
        {
            throw new InvalidOperationException(
                "assetPath is required."
            );
        }


        if (
            !operation.assetPath.StartsWith(
                "Assets/",
                StringComparison.Ordinal
            )
        )
        {
            throw new InvalidOperationException(
                "Script assetPath must be inside Assets/."
            );
        }


        if (
            !operation.assetPath.EndsWith(
                ".cs",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new InvalidOperationException(
                "Script assetPath must end in .cs."
            );
        }


        string fullPath =
            Path.GetFullPath(
                operation.assetPath
            );


        string assetsRoot =
            Path.GetFullPath(
                Application.dataPath
            );


        string projectRoot =
            Directory.GetParent(
                assetsRoot
            ).FullName;


        fullPath =
            Path.GetFullPath(
                Path.Combine(
                    projectRoot,
                    operation.assetPath
                )
            );


        if (
            !fullPath.StartsWith(
                assetsRoot,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new InvalidOperationException(
                "Script path escaped Assets directory."
            );
        }


        string directory =
            Path.GetDirectoryName(
                fullPath
            );


        if (
            !Directory.Exists(
                directory
            )
        )
        {
            Directory.CreateDirectory(
                directory
            );
        }


        File.WriteAllText(
            fullPath,
            operation.content
            ??
            "",
            new UTF8Encoding(
                false
            )
        );


        AssetDatabase.ImportAsset(
            operation.assetPath,
            ImportAssetOptions.ForceUpdate
        );


        return
            "Created script "
            +
            operation.assetPath;
    }


    // ============================================================
    // FIND GAMEOBJECT BY EXACT HIERARCHY PATH
    // ============================================================

    private static GameObject FindGameObject(
        string objectPath
    )
    {
        if (
            string.IsNullOrWhiteSpace(
                objectPath
            )
        )
        {
            throw new InvalidOperationException(
                "objectPath is required."
            );
        }


        string[] parts =
            objectPath.Split(
                '/'
            );


        GameObject current =
            null;


        GameObject[] roots =
            UnityEngine.SceneManagement
                .SceneManager
                .GetActiveScene()
                .GetRootGameObjects();


        foreach (
            GameObject root
            in roots
        )
        {
            if (
                root.name ==
                parts[0]
            )
            {
                current =
                    root;

                break;
            }
        }


        if (
            current ==
            null
        )
        {
            throw new InvalidOperationException(
                "GameObject not found: "
                +
                objectPath
            );
        }


        for (
            int i = 1;
            i < parts.Length;
            i++
        )
        {
            Transform child =
                current.transform.Find(
                    parts[i]
                );


            if (
                child ==
                null
            )
            {
                throw new InvalidOperationException(
                    "GameObject not found: "
                    +
                    objectPath
                );
            }


            current =
                child.gameObject;
        }


        return
            current;
    }


    // ============================================================
    // TYPE LOOKUP
    // ============================================================

    private static Type FindType(
        string typeName
    )
    {
        if (
            string.IsNullOrWhiteSpace(
                typeName
            )
        )
        {
            return
                null;
        }


        Type direct =
            Type.GetType(
                typeName
            );


        if (
            direct != null
        )
        {
            return
                direct;
        }


        foreach (
            System.Reflection.Assembly assembly
            in AppDomain.CurrentDomain.GetAssemblies()
        )
        {
            Type type =
                assembly.GetType(
                    typeName
                );


            if (
                type != null
            )
            {
                return
                    type;
            }


            try
            {
                foreach (
                    Type candidate
                    in assembly.GetTypes()
                )
                {
                    if (
                        candidate.Name ==
                        typeName
                    )
                    {
                        return
                            candidate;
                    }
                }
            }
            catch
            {
                // Some Unity assemblies cannot enumerate every type.
            }
        }


        return
            null;
    }


    // ============================================================
    // PATH
    // ============================================================

    private static string GetHierarchyPath(
        Transform transform
    )
    {
        string path =
            transform.name;


        Transform parent =
            transform.parent;


        while (
            parent != null
        )
        {
            path =
                parent.name
                +
                "/"
                +
                path;


            parent =
                parent.parent;
        }


        return
            path;
    }


    // ============================================================
    // RESPONSE
    // ============================================================

    private static void WriteResponse(
        HttpListenerContext context,
        int statusCode,
        string json
    )
    {
        try
        {
            byte[] bytes =
                Encoding.UTF8.GetBytes(
                    json
                );


            context.Response.StatusCode =
                statusCode;


            context.Response.ContentType =
                "application/json";


            context.Response.ContentEncoding =
                Encoding.UTF8;


            context.Response.ContentLength64 =
                bytes.Length;


            context.Response.OutputStream.Write(
                bytes,
                0,
                bytes.Length
            );


            context.Response.OutputStream.Close();
        }
        catch
        {
        }
    }


    // ============================================================
    // DTO
    // ============================================================

    [Serializable]
    private class BatchRequest
    {
        public BatchOperation[] operations;

        public bool stopOnFailure =
            true;

        public bool saveScene =
            false;
    }


    [Serializable]
    private class BatchOperation
    {
        public string operation;


        // Object identifiers
        public string objectPath;

        public string parentPath;

        public string name;

        public string newName;


        // Primitive
        public string primitiveType;


        // Component
        public string componentType;

        public string propertyName;


        // Generic serialized value
        public string valueType;

        public int intValue;

        public float floatValue;

        public bool boolValue;

        public string stringValue;


        // Vector
        public float x;

        public float y;

        public float z;


        // Color
        public float r;

        public float g;

        public float b;

        public float a =
            1f;


        // Script
        public string assetPath;

        public string content;
    }


    [Serializable]
    private class BatchResponse
    {
        public bool success;

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
}