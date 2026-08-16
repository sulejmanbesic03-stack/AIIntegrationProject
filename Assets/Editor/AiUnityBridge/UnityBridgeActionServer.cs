using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;


[InitializeOnLoad]
public static class UnityBridgeActionServer
{
    private const int Port =
        47822;


    private const string RequiredHeaderValue =
        "AI-Assistant-Local";


    private static readonly ConcurrentQueue<ActionRequest> pendingRequests =
        new ConcurrentQueue<ActionRequest>();


    private static TcpListener listener;

    private static Thread listenerThread;

    private static bool isRunning;


    static UnityBridgeActionServer()
    {
        EditorApplication.update +=
            ProcessPendingRequests;


        AssemblyReloadEvents.beforeAssemblyReload +=
            StopServer;


        EditorApplication.quitting +=
            StopServer;


        StartServer();
    }


    private static void StartServer()
    {
        if (isRunning)
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


            isRunning =
                true;


            listenerThread =
                new Thread(
                    ListenLoop
                );


            listenerThread.IsBackground =
                true;


            listenerThread.Name =
                "AI Assistant Unity Action Server";


            listenerThread.Start();


            Debug.Log(
                $"AI Unity Action Server sluša na http://127.0.0.1:{Port}/"
            );
        }
        catch (Exception ex)
        {
            isRunning =
                false;


            Debug.LogError(
                $"AI Unity Action Server nije pokrenut: {ex.Message}"
            );
        }
    }


    private static void StopServer()
    {
        isRunning =
            false;


        try
        {
            listener?.Stop();
        }
        catch
        {
            // Unity se gasi ili ponovo učitava skripte.
        }
    }


    private static void ListenLoop()
    {
        while (isRunning)
        {
            try
            {
                TcpClient client =
                    listener.AcceptTcpClient();


                ActionRequest request =
                    ReadRequest(
                        client
                    );


                pendingRequests.Enqueue(
                    request
                );
            }
            catch (SocketException)
            {
                if (isRunning)
                {
                    Debug.LogError(
                        "AI Unity Action Server je neočekivano zaustavljen."
                    );
                }
            }
            catch (Exception ex)
            {
                if (isRunning)
                {
                    Debug.LogError(
                        $"AI Unity Action Server greška: {ex.Message}"
                    );
                }
            }
        }
    }


    private static void ProcessPendingRequests()
    {
        while (
            pendingRequests.TryDequeue(
                out ActionRequest request
            )
        )
        {
            ProcessRequest(
                request
            );
        }
    }


    private static void ProcessRequest(
        ActionRequest request
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
                    "Only POST requests are supported."
                );


                return;
            }


            if (
                !request.BridgeHeader.Equals(
                    RequiredHeaderValue,
                    StringComparison.Ordinal
                )
            )
            {
                WriteError(
                    request.Client,
                    403,
                    "Missing or invalid X-AI-Bridge header."
                );


                return;
            }


            string path =
                request.Path
                    .TrimEnd('/')
                    .ToLowerInvariant();


            if (path == "/create-gameobject")
            {
                CreateGameObject(
                    request
                );


                return;
            }

            if (path == "/set-transform")
            {
                SetTransform(
                    request
                );


                return;
            }

            WriteError(
                request.Client,
                404,
                $"Unknown action endpoint: {path}"
            );
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
                // Client je možda zatvorio konekciju.
            }
        }
    }


    // ============================================
    // CREATE GAMEOBJECT
    // ============================================

    private static void CreateGameObject(
        ActionRequest request
    )
    {
        CreateGameObjectRequest data =
            JsonUtility.FromJson<CreateGameObjectRequest>(
                request.Body
            );


        if (
            data == null
            ||
            string.IsNullOrWhiteSpace(
                data.name
            )
        )
        {
            WriteError(
                request.Client,
                400,
                "GameObject name is required."
            );


            return;
        }


        GameObject parent =
            null;


        if (
            !string.IsNullOrWhiteSpace(
                data.parentPath
            )
        )
        {
            parent =
                FindGameObjectByPath(
                    data.parentPath
                );


            if (parent == null)
            {
                WriteError(
                    request.Client,
                    404,
                    $"Parent GameObject nije pronađen: {data.parentPath}"
                );


                return;
            }
        }


        GameObject created =
            new GameObject(
                data.name.Trim()
            );


        Undo.RegisterCreatedObjectUndo(
            created,
            $"AI Create GameObject: {created.name}"
        );


        if (parent != null)
        {
            Undo.SetTransformParent(
                created.transform,
                parent.transform,
                "AI Set GameObject Parent"
            );
        }


        Selection.activeGameObject =
            created;


        EditorSceneManager.MarkSceneDirty(
            created.scene
        );


        string createdPath =
            BuildHierarchyPath(
                created.transform
            );


        CreateGameObjectResponse response =
            new CreateGameObjectResponse
            {
                success = true,

                name = created.name,

                hierarchyPath = createdPath,

                parentPath =
                    parent == null
                        ? ""
                        : BuildHierarchyPath(
                            parent.transform
                        )
            };


        WriteResponse(
            request.Client,
            200,
            JsonUtility.ToJson(
                response,
                true
            )
        );
    }

    // ============================================
    // SET TRANSFORM
    // ============================================

    private static void SetTransform(
        ActionRequest request
    )
    {
        SetTransformRequest data =
            JsonUtility.FromJson<SetTransformRequest>(
                request.Body
            );


        if (
            data == null
            ||
            string.IsNullOrWhiteSpace(
                data.objectPath
            )
        )
        {
            WriteError(
                request.Client,
                400,
                "objectPath is required."
            );


            return;
        }


        GameObject gameObject =
            FindGameObjectByPath(
                data.objectPath
            );


        if (gameObject == null)
        {
            WriteError(
                request.Client,
                404,
                $"GameObject nije pronađen: {data.objectPath}"
            );


            return;
        }


        Transform transform =
            gameObject.transform;


        Undo.RecordObject(
            transform,
            $"AI Set Transform: {data.objectPath}"
        );


        transform.position =
            new Vector3(
                data.positionX,
                data.positionY,
                data.positionZ
            );


        transform.eulerAngles =
            new Vector3(
                data.rotationX,
                data.rotationY,
                data.rotationZ
            );


        transform.localScale =
            new Vector3(
                data.scaleX,
                data.scaleY,
                data.scaleZ
            );


        EditorUtility.SetDirty(
            transform
        );


        EditorSceneManager.MarkSceneDirty(
            gameObject.scene
        );


        SetTransformResponse response =
            new SetTransformResponse
            {
                success = true,

                objectPath =
                    BuildHierarchyPath(
                        transform
                    ),

                position =
                    transform.position,

                rotation =
                    transform.eulerAngles,

                scale =
                    transform.localScale
            };


        WriteResponse(
            request.Client,
            200,
            JsonUtility.ToJson(
                response,
                true
            )
        );
    }

    // ============================================
    // FIND GAMEOBJECT
    // ============================================

    private static GameObject FindGameObjectByPath(
        string hierarchyPath
    )
    {
        if (
            string.IsNullOrWhiteSpace(
                hierarchyPath
            )
        )
        {
            return null;
        }


        string[] parts =
            hierarchyPath
                .Trim('/')
                .Split('/');


        if (parts.Length == 0)
        {
            return null;
        }


        Scene scene =
            SceneManager.GetActiveScene();


        GameObject current =
            null;


        GameObject[] roots =
            scene.GetRootGameObjects();


        foreach (GameObject root in roots)
        {
            if (
                root.name.Equals(
                    parts[0],
                    StringComparison.Ordinal
                )
            )
            {
                current =
                    root;

                break;
            }
        }


        if (current == null)
        {
            return null;
        }


        for (
            int partIndex = 1;
            partIndex < parts.Length;
            partIndex++
        )
        {
            Transform foundChild =
                null;


            for (
                int childIndex = 0;
                childIndex < current.transform.childCount;
                childIndex++
            )
            {
                Transform child =
                    current.transform.GetChild(
                        childIndex
                    );


                if (
                    child.name.Equals(
                        parts[partIndex],
                        StringComparison.Ordinal
                    )
                )
                {
                    foundChild =
                        child;

                    break;
                }
            }


            if (foundChild == null)
            {
                return null;
            }


            current =
                foundChild.gameObject;
        }


        return current;
    }


    private static string BuildHierarchyPath(
        Transform transform
    )
    {
        string path =
            transform.name;


        Transform parent =
            transform.parent;


        while (parent != null)
        {
            path =
                parent.name +
                "/" +
                path;


            parent =
                parent.parent;
        }


        return path;
    }


    // ============================================
    // READ HTTP REQUEST
    // ============================================

    private static ActionRequest ReadRequest(
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


        string requestLine =
            reader.ReadLine();


        if (string.IsNullOrWhiteSpace(requestLine))
        {
            throw new InvalidDataException(
                "HTTP request je prazan."
            );
        }


        string[] requestParts =
            requestLine.Split(' ');


        if (requestParts.Length < 2)
        {
            throw new InvalidDataException(
                "HTTP request line nije ispravan."
            );
        }


        int contentLength =
            0;


        string bridgeHeader =
            "";


        while (true)
        {
            string headerLine =
                reader.ReadLine();


            if (string.IsNullOrEmpty(headerLine))
            {
                break;
            }


            int separatorIndex =
                headerLine.IndexOf(':');


            if (separatorIndex <= 0)
            {
                continue;
            }


            string headerName =
                headerLine
                    .Substring(
                        0,
                        separatorIndex
                    )
                    .Trim();


            string headerValue =
                headerLine
                    .Substring(
                        separatorIndex + 1
                    )
                    .Trim();


            if (
                headerName.Equals(
                    "Content-Length",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                int.TryParse(
                    headerValue,
                    out contentLength
                );
            }


            if (
                headerName.Equals(
                    "X-AI-Bridge",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                bridgeHeader =
                    headerValue;
            }
        }


        char[] bodyBuffer =
            new char[
                contentLength
            ];


        int totalRead =
            0;


        while (totalRead < contentLength)
        {
            int read =
                reader.Read(
                    bodyBuffer,
                    totalRead,
                    contentLength - totalRead
                );


            if (read <= 0)
            {
                break;
            }


            totalRead +=
                read;
        }


        string body =
            new string(
                bodyBuffer,
                0,
                totalRead
            );


        return
            new ActionRequest(
                client,
                requestParts[0],
                requestParts[1].Split('?')[0],
                bridgeHeader,
                body
            );
    }


    // ============================================
    // HTTP RESPONSE
    // ============================================

    private static void WriteError(
        TcpClient client,
        int statusCode,
        string message
    )
    {
        ErrorResponse response =
            new ErrorResponse
            {
                success = false,

                error = message
            };


        WriteResponse(
            client,
            statusCode,
            JsonUtility.ToJson(
                response,
                true
            )
        );
    }


    private static void WriteResponse(
        TcpClient client,
        int statusCode,
        string body
    )
    {
        byte[] bodyBytes =
            Encoding.UTF8.GetBytes(
                body
            );


        string statusText;


        switch (statusCode)
        {
            case 200:

                statusText = "OK";

                break;


            case 400:

                statusText = "Bad Request";

                break;


            case 403:

                statusText = "Forbidden";

                break;


            case 404:

                statusText = "Not Found";

                break;


            case 405:

                statusText = "Method Not Allowed";

                break;


            default:

                statusText = "Internal Server Error";

                break;
        }


        string headers =
            $"HTTP/1.1 {statusCode} {statusText}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n" +
            "\r\n";


        byte[] headerBytes =
            Encoding.ASCII.GetBytes(
                headers
            );


        using (client)
        using (
            NetworkStream stream =
                client.GetStream()
        )
        {
            stream.Write(
                headerBytes,
                0,
                headerBytes.Length
            );


            stream.Write(
                bodyBytes,
                0,
                bodyBytes.Length
            );
        }
    }


    // ============================================
    // DATA CLASSES
    // ============================================

    private class ActionRequest
    {
        public TcpClient Client { get; }

        public string Method { get; }

        public string Path { get; }

        public string BridgeHeader { get; }

        public string Body { get; }


        public ActionRequest(
            TcpClient client,
            string method,
            string path,
            string bridgeHeader,
            string body
        )
        {
            Client = client;

            Method = method;

            Path = path;

            BridgeHeader = bridgeHeader;

            Body = body;
        }
    }


    [Serializable]
    private class CreateGameObjectRequest
    {
        public string name;

        public string parentPath;
    }


    [Serializable]
    private class CreateGameObjectResponse
    {
        public bool success;

        public string name;

        public string hierarchyPath;

        public string parentPath;
    }
    [Serializable]
    private class SetTransformRequest
    {
        public string objectPath;

        public float positionX;

        public float positionY;

        public float positionZ;

        public float rotationX;

        public float rotationY;

        public float rotationZ;

        public float scaleX;

        public float scaleY;

        public float scaleZ;
    }


    [Serializable]
    private class SetTransformResponse
    {
        public bool success;

        public string objectPath;

        public Vector3 position;

        public Vector3 rotation;

        public Vector3 scale;
    }

    [Serializable]
    private class ErrorResponse
    {
        public bool success;

        public string error;
    }
}