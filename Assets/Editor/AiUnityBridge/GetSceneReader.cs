using System;
using System.Linq;

using UnityEngine;
using UnityEngine.SceneManagement;


public static class UnitySceneReader
{
    public static string GetSceneHierarchyJson()
    {
        Scene activeScene =
            SceneManager.GetActiveScene();


        GameObject[] rootObjects =
            activeScene.GetRootGameObjects();


        SceneHierarchyResponse response =
            new SceneHierarchyResponse
            {
                sceneName = activeScene.name,

                scenePath = activeScene.path,

                rootCount = rootObjects.Length,

                roots =
                    rootObjects
                        .Select(
                            CreateGameObjectData
                        )
                        .ToArray()
            };


        return
            JsonUtility.ToJson(
                response,
                true
            );
    }


    private static GameObjectData CreateGameObjectData(
        GameObject gameObject
    )
    {
        Transform objectTransform =
            gameObject.transform;


        GameObjectData[] children =
            new GameObjectData[
                objectTransform.childCount
            ];


        for (
            int i = 0;
            i < objectTransform.childCount;
            i++
        )
        {
            Transform child =
                objectTransform.GetChild(
                    i
                );


            children[i] =
                CreateGameObjectData(
                    child.gameObject
                );
        }


        string[] components =
            gameObject
                .GetComponents<Component>()
                .Where(component =>
                    component != null
                )
                .Select(component =>
                    component
                        .GetType()
                        .Name
                )
                .ToArray();


        return
            new GameObjectData
            {
                name = gameObject.name,

                activeSelf = gameObject.activeSelf,

                activeInHierarchy =
                    gameObject.activeInHierarchy,

                tag = gameObject.tag,

                layer = gameObject.layer,

                components = components,

                children = children
            };
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

        public bool activeSelf;

        public bool activeInHierarchy;

        public string tag;

        public int layer;

        public string[] components;

        public GameObjectData[] children;
    }
}