using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;


[InitializeOnLoad]
public static class UnityBridgeServer
{
    private const int Port =
        47821;


    private const int MaxConsoleErrors =
        100;


    private static readonly ConcurrentQueue<BridgeRequest> pendingRequests =
        new ConcurrentQueue<BridgeRequest>();


    private static readonly object consoleLock =
        new object();


    private static readonly List<ConsoleErrorData> consoleErrors =
        new List<ConsoleErrorData>();


    private static TcpListener listener;

    private static Thread listenerThread;

    private static bool isRunning;


    static UnityBridgeServer()
    {
        EditorApplication.update +=
            ProcessPendingRequests;


        AssemblyReloadEvents.beforeAssemblyReload +=
            StopServer;


        EditorApplication.quitting +=
            StopServer;


        Application.logMessageReceivedThreaded +=
            CaptureConsoleMessage;


        StartServer();
    }


    // ============================================
    // START SERVER
    // ============================================

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


            isRunning = true;


            listenerThread =
                new Thread(
                    ListenLoop
                );


            listenerThread.IsBackground =
                true;


            listenerThread.Name =
                "AI Assistant Unity Bridge";


            listenerThread.Start();


            Debug.Log(
                $"AI Unity Bridge sluša na http://127.0.0.1:{Port}/"
            );
        }
        catch (Exception ex)
        {
            isRunning =
                false;


            Debug.LogError(
                $"AI Unity Bridge nije pokrenut: {ex.Message}"
            );
        }
    }


    // ============================================
    // STOP SERVER
    // ============================================

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


        Application.logMessageReceivedThreaded -=
            CaptureConsoleMessage;
    }


    // ============================================
    // LISTENER THREAD
    // ============================================

    private static void ListenLoop()
    {
        while (isRunning)
        {
            try
            {
                TcpClient client =
                    listener.AcceptTcpClient();


                BridgeRequest request =
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
                        "AI Unity Bridge listener je neočekivano zaustavljen."
                    );
                }
            }
            catch (Exception ex)
            {
                if (isRunning)
                {
                    Debug.LogError(
                        $"AI Unity Bridge greška: {ex.Message}"
                    );
                }
            }
        }
    }


    // ============================================
    // UNITY MAIN THREAD
    // ============================================

    private static void ProcessPendingRequests()
    {
        while (
            pendingRequests.TryDequeue(
                out BridgeRequest request
            )
        )
        {
            ProcessRequest(
                request
            );
        }
    }


    // ============================================
    // ROUTES
    // ============================================

    private static void ProcessRequest(
        BridgeRequest request
    )
    {
        try
        {
            if (
                !request.Method.Equals(
                    "GET",
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                WriteError(
                    request.Client,
                    405,
                    "Only GET requests are supported."
                );


                return;
            }


            string path =
                request.Path
                    .TrimEnd('/')
                    .ToLowerInvariant();


            if (
                path == ""
                ||
                path == "/health"
            )
            {
                WriteResponse(
                    request.Client,
                    200,
                    "{\n" +
                    "  \"status\": \"ok\",\n" +
                    "  \"bridgeVersion\": \"0.2.0\"\n" +
                    "}"
                );


                return;
            }


            if (path == "/active-scene")
            {
                WriteResponse(
                    request.Client,
                    200,
                    GetActiveSceneJson()
                );


                return;
            }


            if (path == "/scene-hierarchy")
            {
                WriteResponse(
                    request.Client,
                    200,
                    GetSceneHierarchyJson()
                );


                return;
            }


            if (path == "/console-errors")
            {
                WriteResponse(
                    request.Client,
                    200,
                    GetConsoleErrorsJson()
                );


                return;
            }


            WriteError(
                request.Client,
                404,
                $"Unknown endpoint: {path}"
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
                // Client je možda prekinuo konekciju.
            }
        }
    }


    // ============================================
    // ACTIVE SCENE
    // ============================================

    private static string GetActiveSceneJson()
    {
        Scene scene =
            SceneManager.GetActiveScene();


        ActiveSceneResponse response =
            new ActiveSceneResponse
            {
                name = scene.name,

                path = scene.path,

                isLoaded = scene.isLoaded,

                isDirty = scene.isDirty,

                rootCount = scene.rootCount
            };


        return
            JsonUtility.ToJson(
                response,
                true
            );
    }


    // ============================================
    // SCENE HIERARCHY
    // ============================================

    private static string GetSceneHierarchyJson()
    {
        Scene scene =
            SceneManager.GetActiveScene();


        GameObject[] rootObjects =
            scene.GetRootGameObjects();


        GameObjectData[] roots =
            rootObjects
                .Select(
                    root =>
                        CreateGameObjectData(
                            root,
                            root.name
                        )
                )
                .ToArray();


        SceneHierarchyResponse response =
            new SceneHierarchyResponse
            {
                sceneName = scene.name,

                scenePath = scene.path,

                rootCount = roots.Length,

                roots = roots
            };


        return
            JsonUtility.ToJson(
                response,
                true
            );
    }


    private static GameObjectData CreateGameObjectData(
        GameObject gameObject,
        string hierarchyPath
    )
    {
        Component[] attachedComponents =
            gameObject.GetComponents<Component>();


        string[] components =
            attachedComponents
                .Select(
                    component =>
                        component == null
                            ? "Missing Script"
                            : component.GetType().FullName
                )
                .ToArray();


        Transform transform =
            gameObject.transform;


        GameObjectData[] children =
            new GameObjectData[
                transform.childCount
            ];


        for (
            int i = 0;
            i < transform.childCount;
            i++
        )
        {
            GameObject child =
                transform
                    .GetChild(i)
                    .gameObject;


            string childPath =
                hierarchyPath +
                "/" +
                child.name;


            children[i] =
                CreateGameObjectData(
                    child,
                    childPath
                );
        }


        return
            new GameObjectData
            {
                name = gameObject.name,

                hierarchyPath = hierarchyPath,

                activeSelf = gameObject.activeSelf,

                activeInHierarchy =
                    gameObject.activeInHierarchy,

                tag = gameObject.tag,

                layer = gameObject.layer,

                position =
                    Vector3ToData(
                        transform.position
                    ),

                rotation =
                    Vector3ToData(
                        transform.eulerAngles
                    ),

                scale =
                    Vector3ToData(
                        transform.localScale
                    ),

                components = components,

                children = children
            };
    }


    private static Vector3Data Vector3ToData(
        Vector3 value
    )
    {
        return
            new Vector3Data
            {
                x = value.x,

                y = value.y,

                z = value.z
            };
    }


    // ============================================
    // CONSOLE ERRORS
    // ============================================

    private static void CaptureConsoleMessage(
        string condition,
        string stackTrace,
        LogType type
    )
    {
        if (
            type != LogType.Error
            &&
            type != LogType.Exception
            &&
            type != LogType.Assert
        )
        {
            return;
        }


        ConsoleErrorData error =
            new ConsoleErrorData
            {
                message = condition,

                stackTrace = stackTrace,

                type = type.ToString(),

                timestampUtc =
                    DateTime.UtcNow.ToString(
                        "O"
                    )
            };


        lock (consoleLock)
        {
            consoleErrors.Add(
                error
            );


            if (
                consoleErrors.Count >
                MaxConsoleErrors
            )
            {
                int removeCount =
                    consoleErrors.Count -
                    MaxConsoleErrors;


                consoleErrors.RemoveRange(
                    0,
                    removeCount
                );
            }
        }
    }


    private static string GetConsoleErrorsJson()
    {
        ConsoleErrorData[] errors;


        lock (consoleLock)
        {
            errors =
                consoleErrors.ToArray();
        }


        ConsoleErrorsResponse response =
            new ConsoleErrorsResponse
            {
                capturedSinceBridgeLoad =
                    true,

                count =
                    errors.Length,

                errors =
                    errors
            };


        return
            JsonUtility.ToJson(
                response,
                true
            );
    }


    // ============================================
    // READ HTTP REQUEST
    // ============================================

    private static BridgeRequest ReadRequest(
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
                Encoding.ASCII,
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


        string[] parts =
            requestLine.Split(' ');


        if (parts.Length < 2)
        {
            throw new InvalidDataException(
                "HTTP request nije ispravan."
            );
        }


        string path =
            parts[1].Split('?')[0];


        return
            new BridgeRequest(
                client,
                parts[0],
                path
            );
    }


    // ============================================
    // WRITE HTTP RESPONSE
    // ============================================

    private static void WriteError(
        TcpClient client,
        int statusCode,
        string message
    )
    {
        ErrorResponse error =
            new ErrorResponse
            {
                error = message
            };


        WriteResponse(
            client,
            statusCode,
            JsonUtility.ToJson(
                error,
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

                statusText =
                    "OK";

                break;


            case 404:

                statusText =
                    "Not Found";

                break;


            case 405:

                statusText =
                    "Method Not Allowed";

                break;


            default:

                statusText =
                    "Internal Server Error";

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

    private class BridgeRequest
    {
        public TcpClient Client { get; }

        public string Method { get; }

        public string Path { get; }


        public BridgeRequest(
            TcpClient client,
            string method,
            string path
        )
        {
            Client =
                client;


            Method =
                method;


            Path =
                path;
        }
    }


    [Serializable]
    private class ErrorResponse
    {
        public string error;
    }


    [Serializable]
    private class ActiveSceneResponse
    {
        public string name;

        public string path;

        public bool isLoaded;

        public bool isDirty;

        public int rootCount;
    }


    [Serializable]
    private class SceneHierarchyResponse
    {
        public string sceneName;

        public string scenePath;

        public int rootCount;

        public GameObjectData[] roots;
    }


    [Serializable]
    private class GameObjectData
    {
        public string name;

        public string hierarchyPath;

        public bool activeSelf;

        public bool activeInHierarchy;

        public string tag;

        public int layer;

        public Vector3Data position;

        public Vector3Data rotation;

        public Vector3Data scale;

        public string[] components;

        public GameObjectData[] children;
    }


    [Serializable]
    private class Vector3Data
    {
        public float x;

        public float y;

        public float z;
    }


    [Serializable]
    private class ConsoleErrorsResponse
    {
        public bool capturedSinceBridgeLoad;

        public int count;

        public ConsoleErrorData[] errors;
    }


    [Serializable]
    private class ConsoleErrorData
    {
        public string message;

        public string stackTrace;

        public string type;

        public string timestampUtc;
    }
}