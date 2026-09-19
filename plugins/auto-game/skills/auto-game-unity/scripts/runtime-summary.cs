using UnityEngine.SceneManagement;
var scene = SceneManager.GetActiveScene();
return new {
    unityVersion = Application.unityVersion,
    productName = Application.productName,
    dataPath = Application.dataPath,
    scene = new { name = scene.name, path = scene.path, buildIndex = scene.buildIndex, loaded = scene.isLoaded },
    rootCount = scene.isLoaded ? scene.rootCount : 0
};
