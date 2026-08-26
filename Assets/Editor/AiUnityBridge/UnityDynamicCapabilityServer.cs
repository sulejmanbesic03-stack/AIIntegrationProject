using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;

using UnityEngine;
using UnityEngine.SceneManagement;


public interface IUnityDynamicCapability
{
    string Name { get; }

    string Execute(
        UnityDynamicCapabilityContext context,
        string argumentsJson
    );
}


public sealed class UnityDynamicCapabilityContext
{
    public GameObject FindRequired(string hierarchyPath)
    {
        if (string.IsNullOrWhiteSpace(hierarchyPath))
        {
            throw new ArgumentException(
                "Hierarchy path is required.",
                nameof(hierarchyPath)
            );
        }

        string[] parts =
            hierarchyPath
                .Replace('\\', '/')
                .Split(
                    new[] { '/' },
                    StringSplitOptions.RemoveEmptyEntries
                );

        if (parts.Length == 0)
        {
            throw new InvalidOperationException(
                "Hierarchy path is empty."
            );
        }

        Scene scene =
            SceneManager.GetActiveScene();

        if (!scene.IsValid())
        {
            throw new InvalidOperationException(
                "Active Unity scene is not valid."
            );
        }

        GameObject current = null;

        foreach (GameObject root in scene.GetRootGameObjects())
        {
            if (
                string.Equals(
                    root.name,
                    parts[0],
                    StringComparison.Ordinal
                )
            )
            {
                current = root;
                break;
            }
        }

        if (current == null)
        {
            throw new InvalidOperationException(
                "GameObject not found: " + hierarchyPath
            );
        }

        for (int i = 1; i < parts.Length; i++)
        {
            Transform child =
                current.transform.Find(parts[i]);

            if (child == null)
            {
                throw new InvalidOperationException(
                    "GameObject not found: " + hierarchyPath
                );
            }

            current = child.gameObject;
        }

        return current;
    }


    public T GetRequiredComponent<T>(GameObject target)
        where T : Component
    {
        if (target == null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        T component =
            target.GetComponent<T>();

        if (component == null)
        {
            throw new InvalidOperationException(
                target.name
                + " does not contain component "
                + typeof(T).FullName
                + "."
            );
        }

        return component;
    }


    public T GetOrAddComponent<T>(GameObject target)
        where T : Component
    {
        if (target == null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        T component =
            target.GetComponent<T>();

        if (component != null)
        {
            return component;
        }

        return Undo.AddComponent<T>(target);
    }


    public GameObject CreateGameObject(
        string name,
        Transform parent = null
    )
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                "GameObject name is required.",
                nameof(name)
            );
        }

        GameObject created =
            new GameObject(name);

        Undo.RegisterCreatedObjectUndo(
            created,
            "AI create " + name
        );

        if (parent != null)
        {
            Undo.SetTransformParent(
                created.transform,
                parent,
                "AI parent " + name
            );
        }

        return created;
    }


    public GameObject CreatePrimitive(
        PrimitiveType primitiveType,
        string name,
        Transform parent = null
    )
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException(
                "GameObject name is required.",
                nameof(name)
            );
        }

        GameObject created =
            GameObject.CreatePrimitive(primitiveType);

        created.name = name;

        Undo.RegisterCreatedObjectUndo(
            created,
            "AI create " + name
        );

        if (parent != null)
        {
            Undo.SetTransformParent(
                created.transform,
                parent,
                "AI parent " + name
            );
        }

        return created;
    }


    public void Record(
        UnityEngine.Object target,
        string actionName
    )
    {
        if (target == null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        Undo.RecordObject(
            target,
            string.IsNullOrWhiteSpace(actionName)
                ? "AI capability change"
                : actionName
        );
    }


    public void MarkDirty(UnityEngine.Object target)
    {
        if (target == null)
        {
            throw new ArgumentNullException(nameof(target));
        }

        EditorUtility.SetDirty(target);
    }


    public void SaveActiveScene()
    {
        Scene scene =
            SceneManager.GetActiveScene();

        if (!scene.IsValid())
        {
            throw new InvalidOperationException(
                "Active Unity scene is not valid."
            );
        }

        if (string.IsNullOrWhiteSpace(scene.path))
        {
            throw new InvalidOperationException(
                "Active scene has no path. Save it manually once first."
            );
        }

        if (!EditorSceneManager.SaveScene(scene))
        {
            throw new InvalidOperationException(
                "Unity could not save the active scene."
            );
        }
    }
}


[InitializeOnLoad]
public static class UnityDynamicCapabilityServer
{
    private const string Prefix =
        "http://127.0.0.1:47825/";

    private const string RequiredHeader =
        "AI-Assistant-Local";

    private const int MaxSourceChars =
        40000;

    private const int MaxLoadedCapabilities =
        12;

    private static readonly ConcurrentQueue<PendingRequest>
        pendingRequests =
            new ConcurrentQueue<PendingRequest>();

    private static readonly Dictionary<string, Type>
        compiledTypes =
            new Dictionary<string, Type>(StringComparer.Ordinal);

    private static readonly string[] BlockedSourceFragments =
    {
        "System.IO",
        "System.Net",
        "System.Diagnostics",
        "Microsoft.Win32",
        "System.Reflection",
        "System.Runtime.InteropServices",
        "DllImport",
        "Assembly.Load",
        "AssemblyLoadContext",
        "AppDomain",
        "Environment.",
        "Process.",
        "File.",
        "Directory.",
        "HttpClient",
        "WebClient",
        "Thread",
        "Task.Run",
        "unsafe",
        "stackalloc",
        "UnityEditor",
        "Destroy(",
        "DestroyImmediate(",
        "Application.Quit",
        "AssetDatabase.DeleteAsset",
        "FileUtil",
        "BuildPipeline",
        "PlayerSettings",
        "PackageManager"
    };

    private static HttpListener listener;
    private static Thread listenerThread;
    private static volatile bool running;
    private static PendingRequest activeRequest;
    private static AssemblyBuilder activeBuilder;
    private static int loadedCapabilityCount;


    static UnityDynamicCapabilityServer()
    {
        EditorApplication.update += ProcessPendingRequests;
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

            listenerThread =
                new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "AI Unity Dynamic Capability Server"
                };

            listenerThread.Start();

            Debug.Log(
                "[AI Dynamic Capability] Listening on 127.0.0.1:47825"
            );
        }
        catch (Exception ex)
        {
            running = false;

            Debug.LogError(
                "[AI Dynamic Capability] Start failed: "
                + ex.Message
            );
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
            // Unity is closing or reloading editor assemblies.
        }

        FailPendingRequests(
            "Unity is reloading assemblies or closing."
        );
    }


    private static void ListenLoop()
    {
        while (running)
        {
            try
            {
                HttpListenerContext httpContext =
                    listener.GetContext();

                HandleHttpRequest(httpContext);
            }
            catch (Exception ex)
            {
                if (running)
                {
                    Debug.LogError(
                        "[AI Dynamic Capability] Listener error: "
                        + ex.Message
                    );
                }
            }
        }
    }


    private static void HandleHttpRequest(
        HttpListenerContext httpContext
    )
    {
        try
        {
            if (
                httpContext.Request.Headers["X-AI-Bridge"]
                != RequiredHeader
            )
            {
                WriteResponse(
                    httpContext,
                    403,
                    Failure("authorization", "Unauthorized bridge request.")
                );
                return;
            }

            string path =
                httpContext.Request.Url
                    .AbsolutePath
                    .TrimEnd('/')
                    .ToLowerInvariant();

            if (
                httpContext.Request.HttpMethod == "GET"
                && path == "/status"
            )
            {
                WriteResponse(
                    httpContext,
                    200,
                    new CapabilityResponse
                    {
                        success = true,
                        phase = "ready",
                        message =
                            "Unity dynamic capability server is ready.",
                        loadedCapabilities = loadedCapabilityCount
                    }
                );
                return;
            }

            if (
                httpContext.Request.HttpMethod != "POST"
                || path != "/execute-capability"
            )
            {
                WriteResponse(
                    httpContext,
                    404,
                    Failure("request", "Unknown capability endpoint.")
                );
                return;
            }

            string body;

            using (
                StreamReader reader =
                    new StreamReader(
                        httpContext.Request.InputStream,
                        httpContext.Request.ContentEncoding
                    )
            )
            {
                body = reader.ReadToEnd();
            }

            CapabilityRequest request =
                JsonUtility.FromJson<CapabilityRequest>(body);

            string validationError =
                ValidateRequest(request);

            if (validationError != null)
            {
                WriteResponse(
                    httpContext,
                    400,
                    Failure("validation", validationError)
                );
                return;
            }

            pendingRequests.Enqueue(
                new PendingRequest(httpContext, request)
            );
        }
        catch (Exception ex)
        {
            WriteResponse(
                httpContext,
                500,
                Failure(
                    "request",
                    ex.GetType().Name + ": " + ex.Message
                )
            );
        }
    }


    private static void ProcessPendingRequests()
    {
        if (activeRequest != null)
        {
            return;
        }

        if (!pendingRequests.TryDequeue(out PendingRequest pending))
        {
            return;
        }

        activeRequest = pending;

        try
        {
            string sourceHash =
                ComputeSourceHash(pending.request.source);

            if (
                compiledTypes.TryGetValue(
                    sourceHash,
                    out Type cachedType
                )
            )
            {
                ExecuteCompiledType(
                    pending,
                    cachedType,
                    sourceHash,
                    fromCache: true
                );
                activeRequest = null;
                return;
            }

            if (
                loadedCapabilityCount
                >= MaxLoadedCapabilities
            )
            {
                WriteResponse(
                    pending.httpContext,
                    409,
                    Failure(
                        "limit",
                        "Dynamic capability session limit reached ("
                        + MaxLoadedCapabilities
                        + "). Restart or recompile Unity scripts to clear the in-memory cache."
                    )
                );
                activeRequest = null;
                return;
            }

            BeginCompilation(
                pending,
                sourceHash
            );
        }
        catch (Exception ex)
        {
            WriteResponse(
                pending.httpContext,
                500,
                Failure(
                    "compile-start",
                    ex.GetType().Name + ": " + ex.Message
                )
            );
            activeRequest = null;
            activeBuilder = null;
        }
    }


    private static void BeginCompilation(
        PendingRequest pending,
        string sourceHash
    )
    {
        string root =
            Path.Combine(
                Directory.GetCurrentDirectory(),
                "Library",
                "AIUnityBridge",
                "DynamicCapabilities",
                sourceHash
            );

        Directory.CreateDirectory(root);

        string sourcePath =
            Path.Combine(root, pending.request.name + ".cs");

        string dllPath =
            Path.Combine(root, pending.request.name + ".dll");

        File.WriteAllText(
            sourcePath,
            pending.request.source,
            new UTF8Encoding(false)
        );

#pragma warning disable 0618
        AssemblyBuilder builder =
            new AssemblyBuilder(
                dllPath,
                new[] { sourcePath }
            );

        builder.flags =
            AssemblyBuilderFlags.EditorAssembly;

        builder.referencesOptions =
            ReferencesOptions.UseEngineModules;

        string hostAssemblyPath =
            typeof(IUnityDynamicCapability)
                .Assembly
                .Location;

        if (!string.IsNullOrWhiteSpace(hostAssemblyPath))
        {
            builder.additionalReferences =
                new[] { hostAssemblyPath };
        }

        builder.buildFinished +=
            (assemblyPath, compilerMessages) =>
                OnCompilationFinished(
                    pending,
                    sourceHash,
                    sourcePath,
                    assemblyPath,
                    compilerMessages
                );

        activeBuilder = builder;

        bool started =
            builder.Build();
#pragma warning restore 0618

        if (!started)
        {
            activeBuilder = null;
            activeRequest = null;

            WriteResponse(
                pending.httpContext,
                503,
                Failure(
                    "busy",
                    "Unity is already compiling project scripts. Wait for compilation to finish, then send one new request."
                )
            );
        }
    }


    private static void OnCompilationFinished(
        PendingRequest pending,
        string sourceHash,
        string sourcePath,
        string assemblyPath,
        CompilerMessage[] compilerMessages
    )
    {
        try
        {
            CompilerMessage[] errors =
                (compilerMessages ?? Array.Empty<CompilerMessage>())
                    .Where(message =>
                        message.type == CompilerMessageType.Error
                    )
                    .Take(12)
                    .ToArray();

            if (errors.Length > 0)
            {
                WriteResponse(
                    pending.httpContext,
                    400,
                    new CapabilityResponse
                    {
                        success = false,
                        phase = "compile",
                        message =
                            "Unity capability compile failed. Fix all diagnostics in one complete source rewrite.",
                        sourceHash = sourceHash,
                        diagnostics =
                            errors
                                .Select(ToDiagnostic)
                                .ToArray(),
                        loadedCapabilities = loadedCapabilityCount
                    }
                );
                return;
            }

            if (!File.Exists(assemblyPath))
            {
                WriteResponse(
                    pending.httpContext,
                    500,
                    Failure(
                        "load",
                        "Unity compiler reported success but did not create the capability DLL."
                    )
                );
                return;
            }

            byte[] assemblyBytes =
                File.ReadAllBytes(assemblyPath);

            System.Reflection.Assembly assembly =
        System.Reflection.Assembly.Load(
            assemblyBytes
        );

            Type[] capabilityTypes =
                assembly
                    .GetTypes()
                    .Where(type =>
                        !type.IsAbstract
                        && !type.IsInterface
                        && typeof(IUnityDynamicCapability)
                            .IsAssignableFrom(type)
                    )
                    .ToArray();

            if (capabilityTypes.Length != 1)
            {
                WriteResponse(
                    pending.httpContext,
                    400,
                    Failure(
                        "contract",
                        "Source must contain exactly one concrete IUnityDynamicCapability implementation."
                    )
                );
                return;
            }

            Type capabilityType =
                capabilityTypes[0];

            compiledTypes[sourceHash] = capabilityType;
            loadedCapabilityCount++;

            ExecuteCompiledType(
                pending,
                capabilityType,
                sourceHash,
                fromCache: false
            );
        }
        catch (ReflectionTypeLoadException ex)
        {
            string loaderErrors =
                string.Join(
                    " | ",
                    ex.LoaderExceptions
                        .Where(item => item != null)
                        .Take(6)
                        .Select(item => item.Message)
                );

            WriteResponse(
                pending.httpContext,
                500,
                Failure("load", loaderErrors)
            );
        }
        catch (Exception ex)
        {
            WriteResponse(
                pending.httpContext,
                500,
                Failure(
                    "load",
                    ex.GetType().Name + ": " + ex.Message
                )
            );
        }
        finally
        {
            TryDelete(sourcePath);
            TryDelete(assemblyPath);
            TryDelete(Path.ChangeExtension(assemblyPath, ".pdb"));

            activeBuilder = null;
            activeRequest = null;
        }
    }


    private static void ExecuteCompiledType(
        PendingRequest pending,
        Type capabilityType,
        string sourceHash,
        bool fromCache
    )
    {
        Undo.IncrementCurrentGroup();

        int undoGroup =
            Undo.GetCurrentGroup();

        Undo.SetCurrentGroupName(
            "AI capability: " + pending.request.name
        );

        try
        {
            if (
                Activator.CreateInstance(capabilityType)
                is not IUnityDynamicCapability capability
            )
            {
                throw new InvalidOperationException(
                    "Could not instantiate IUnityDynamicCapability."
                );
            }

            if (
                !string.Equals(
                    capability.Name,
                    pending.request.name,
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidOperationException(
                    "Requested name '"
                    + pending.request.name
                    + "' does not match generated Name '"
                    + capability.Name
                    + "'."
                );
            }

            UnityDynamicCapabilityContext capabilityContext =
                new UnityDynamicCapabilityContext();

            string result =
                capability.Execute(
                    capabilityContext,
                    string.IsNullOrWhiteSpace(
                        pending.request.argumentsJson
                    )
                        ? "{}"
                        : pending.request.argumentsJson
                );

            Undo.CollapseUndoOperations(undoGroup);

            WriteResponse(
                pending.httpContext,
                200,
                new CapabilityResponse
                {
                    success = true,
                    phase = "execute",
                    message = string.IsNullOrWhiteSpace(result)
                        ? "Unity capability completed successfully."
                        : result,
                    sourceHash = sourceHash,
                    fromCache = fromCache,
                    loadedCapabilities = loadedCapabilityCount
                }
            );
        }
        catch (Exception ex)
        {
            try
            {
                Undo.RevertAllDownToGroup(undoGroup);
            }
            catch
            {
                // Report the original capability exception.
            }

            WriteResponse(
                pending.httpContext,
                500,
                new CapabilityResponse
                {
                    success = false,
                    phase = "execute",
                    message =
                        ex.GetType().Name + ": " + ex.Message,
                    sourceHash = sourceHash,
                    fromCache = fromCache,
                    loadedCapabilities = loadedCapabilityCount
                }
            );
        }
    }


    private static string ValidateRequest(
        CapabilityRequest request
    )
    {
        if (request == null)
        {
            return "Request body is invalid.";
        }

        if (
            string.IsNullOrWhiteSpace(request.name)
            || !System.Text.RegularExpressions.Regex.IsMatch(
                request.name,
                "^[A-Za-z][A-Za-z0-9_]{0,63}$"
            )
        )
        {
            return "Capability name must start with a letter and contain only letters, numbers or underscore.";
        }

        if (string.IsNullOrWhiteSpace(request.source))
        {
            return "Capability source is empty.";
        }

        if (request.source.Length > MaxSourceChars)
        {
            return
                "Capability source is too large ("
                + request.source.Length
                + "/"
                + MaxSourceChars
                + " characters).";
        }

        if (
            !request.source.Contains(
                "IUnityDynamicCapability"
            )
        )
        {
            return "Source must implement IUnityDynamicCapability.";
        }

        foreach (string blocked in BlockedSourceFragments)
        {
            if (
                request.source.IndexOf(
                    blocked,
                    StringComparison.OrdinalIgnoreCase
                )
                >= 0
            )
            {
                return
                    "Blocked API/token '"
                    + blocked
                    + "'. UnityEngine component APIs are allowed; OS, network, reflection, UnityEditor and destructive APIs are not.";
            }
        }

        return null;
    }


    private static string ComputeSourceHash(string source)
    {
        using SHA256 sha =
            SHA256.Create();

        byte[] hash =
            sha.ComputeHash(
                Encoding.UTF8.GetBytes(source)
            );

        return
            BitConverter
                .ToString(hash)
                .Replace("-", string.Empty)
                .Substring(0, 20);
    }


    private static CapabilityDiagnostic ToDiagnostic(
        CompilerMessage message
    )
    {
        return
            new CapabilityDiagnostic
            {
                file = Path.GetFileName(message.file),
                line = message.line,
                column = message.column,
                message = message.message
            };
    }


    private static CapabilityResponse Failure(
        string phase,
        string message
    )
    {
        return
            new CapabilityResponse
            {
                success = false,
                phase = phase,
                message = message,
                loadedCapabilities = loadedCapabilityCount
            };
    }


    private static void FailPendingRequests(string message)
    {
        if (activeRequest != null)
        {
            WriteResponse(
                activeRequest.httpContext,
                503,
                Failure("shutdown", message)
            );
        }

        while (
            pendingRequests.TryDequeue(
                out PendingRequest pending
            )
        )
        {
            WriteResponse(
                pending.httpContext,
                503,
                Failure("shutdown", message)
            );
        }
    }


    private static void WriteResponse(
        HttpListenerContext httpContext,
        int statusCode,
        CapabilityResponse response
    )
    {
        if (httpContext == null)
        {
            return;
        }

        try
        {
            string json =
                JsonUtility.ToJson(response);

            byte[] bytes =
                Encoding.UTF8.GetBytes(json);

            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType =
                "application/json; charset=utf-8";
            httpContext.Response.ContentEncoding =
                Encoding.UTF8;
            httpContext.Response.ContentLength64 =
                bytes.Length;

            httpContext.Response.OutputStream.Write(
                bytes,
                0,
                bytes.Length
            );
        }
        catch
        {
            // The external client may have timed out or disconnected.
        }
        finally
        {
            try
            {
                httpContext.Response.OutputStream.Close();
                httpContext.Response.Close();
            }
            catch
            {
                // Response is already closed.
            }
        }
    }


    private static void TryDelete(string path)
    {
        try
        {
            if (
                !string.IsNullOrWhiteSpace(path)
                && File.Exists(path)
            )
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Library cleanup is best effort only.
        }
    }


    [Serializable]
    private sealed class CapabilityRequest
    {
        public string name;
        public string source;
        public string argumentsJson;
    }


    [Serializable]
    private sealed class CapabilityResponse
    {
        public bool success;
        public string phase;
        public string message;
        public string sourceHash;
        public bool fromCache;
        public int loadedCapabilities;
        public CapabilityDiagnostic[] diagnostics;
    }


    [Serializable]
    private sealed class CapabilityDiagnostic
    {
        public string file;
        public int line;
        public int column;
        public string message;
    }


    private sealed class PendingRequest
    {
        public readonly HttpListenerContext httpContext;
        public readonly CapabilityRequest request;

        public PendingRequest(
            HttpListenerContext httpContext,
            CapabilityRequest request
        )
        {
            this.httpContext = httpContext;
            this.request = request;
        }
    }
}
