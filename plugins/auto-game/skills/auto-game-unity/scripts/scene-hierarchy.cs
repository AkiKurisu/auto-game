using UnityEngine.SceneManagement;
AutoGameValue maxDepthValue;
AutoGameValue maxObjectsValue;
var maxDepth = Math.Max(0, Math.Min(16, Args.TryGetValue("maxDepth", out maxDepthValue) ? (int)maxDepthValue.AsInt64() : 4));
var maxObjects = Math.Max(1, Math.Min(5000, Args.TryGetValue("maxObjects", out maxObjectsValue) ? (int)maxObjectsValue.AsInt64() : 500));
var rows = new List<object>();
Action<Transform, int> visit = null;
visit = (transform, depth) => {
    if (rows.Count >= maxObjects || depth > maxDepth) return;
    rows.Add(new { path = Probe.Path(transform), active = transform.gameObject.activeInHierarchy,
        components = transform.GetComponents<Component>().Where(value => value != null).Select(value => value.GetType().FullName).ToArray() });
    foreach (Transform child in transform) visit(child, depth + 1);
};
var scene = SceneManager.GetActiveScene();
foreach (var root in scene.GetRootGameObjects()) visit(root.transform, 0);
return new { scene = scene.name, truncated = rows.Count >= maxObjects, objects = rows };
