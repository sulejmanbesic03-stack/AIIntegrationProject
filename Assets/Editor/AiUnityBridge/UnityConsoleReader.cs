using System;
using System.Collections.Generic;

using UnityEditor;
using UnityEngine;


[InitializeOnLoad]
public static class UnityConsoleReader
{
    private const int MaxStoredErrors = 100;


    private static readonly object errorsLock =
        new object();


    private static readonly List<ConsoleErrorData> errors =
        new List<ConsoleErrorData>();


    static UnityConsoleReader()
    {
        Application.logMessageReceivedThreaded +=
            CaptureLogMessage;


        AssemblyReloadEvents.beforeAssemblyReload +=
            StopCapturing;
    }


    // ============================================
    // CAPTURE UNITY LOG
    // ============================================

    private static void CaptureLogMessage(
        string message,
        string stackTrace,
        LogType logType
    )
    {
        bool isError =
            logType == LogType.Error
            ||
            logType == LogType.Exception
            ||
            logType == LogType.Assert;


        if (!isError)
        {
            return;
        }


        ConsoleErrorData error =
            new ConsoleErrorData
            {
                message = message,

                stackTrace = stackTrace,

                type = logType.ToString(),

                timestampUtc =
                    DateTime.UtcNow.ToString(
                        "O"
                    )
            };


        lock (errorsLock)
        {
            errors.Add(
                error
            );


            if (
                errors.Count >
                MaxStoredErrors
            )
            {
                int removeCount =
                    errors.Count -
                    MaxStoredErrors;


                errors.RemoveRange(
                    0,
                    removeCount
                );
            }
        }
    }


    // ============================================
    // RETURN ERRORS AS JSON
    // ============================================

    public static string GetConsoleErrorsJson()
    {
        ConsoleErrorData[] errorSnapshot;


        lock (errorsLock)
        {
            errorSnapshot =
                errors.ToArray();
        }


        ConsoleErrorsResponse response =
            new ConsoleErrorsResponse
            {
                capturedSinceBridgeLoad = true,

                count = errorSnapshot.Length,

                errors = errorSnapshot
            };


        return
            JsonUtility.ToJson(
                response,
                true
            );
    }


    // ============================================
    // STOP CAPTURING
    // ============================================

    private static void StopCapturing()
    {
        Application.logMessageReceivedThreaded -=
            CaptureLogMessage;
    }


    // ============================================
    // TEST
    // ============================================

    [MenuItem("Window/AI Assistant/Create Test Error")]
    private static void CreateTestError()
    {
        Debug.LogError(
            "AI Assistant Unity Bridge test error."
        );
    }


    // ============================================
    // DATA CLASSES
    // ============================================

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