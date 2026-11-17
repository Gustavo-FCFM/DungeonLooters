using System.Collections.Generic;
using System.Reflection;
using System.Text;
using FishNet.Object;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

// ============================================================
// MercTrainingSetup
//
// Herramienta de UNA VEZ (17 de noviembre de 2026, de las "Mejoras del GAS"): la ESCENA DE PRUEBA DE HABILIDADES.
//   1. Attributes/Testing/ASDef_TrainingDummy: 10 000 de vida, quieto.
//   2. Prefabs/Testing/TrainingDummy: el muñeco (TrainingDummy + ASC, en la capa de personajes para que lo encuentren
//      los ataques), con su cartel (título, números, efectos) que mira a la cámara.
//   3. Scenes/Test_Abilities: una COPIA de Test_Network (trae la red, la sala y los puntos de aparición; esa escena no
//      se toca) con una fila de muñecos en el centro, mirando a donde aparece el jugador: normal, blindado,
//      resistente, con escudo, aliado herido, y un grupo de tres juntos para las de área. A la copia se le rehacen los
//      SceneId de FishNet (si no, chocan con los de la original).
// Se abre la escena aparte (aditiva) y se cierra: la que tengas abierta no cambia. Lo que ya existe no se rehace.
// Después se borra este archivo; los muñecos se mueven y se cambian a mano.
// ============================================================
public static class MercTrainingSetup
{
    private const string StatsPath = "Assets/Attributes/Testing/ASDef_TrainingDummy.asset";
    private const string PrefabPath = "Assets/Prefabs/Testing/TrainingDummy.prefab";
    private const string SourceScene = "Assets/Scenes/Test_Network.unity";
    private const string ScenePath = "Assets/Scenes/Test_Abilities.unity";
    private const int CharacterLayer = 7;

    [MenuItem("Mercenarios/GAS/Una vez: armar la escena de prueba de habilidades")]
    public static void RunMenu() => Debug.Log(Run());

    public static string Run()
    {
        var log = new StringBuilder("[Prueba de habilidades] ");
        AttributeSetDefinition stats = EnsureStats(log);
        GameObject prefab = EnsurePrefab(stats, log);
        BuildScene(prefab, log);
        AssetDatabase.SaveAssets();
        return log.ToString();
    }

    // --- 1. Los stats ---
    private static AttributeSetDefinition EnsureStats(StringBuilder log)
    {
        var stats = AssetDatabase.LoadAssetAtPath<AttributeSetDefinition>(StatsPath);
        if (stats != null) return stats;
        if (!AssetDatabase.IsValidFolder("Assets/Attributes/Testing")) AssetDatabase.CreateFolder("Assets/Attributes", "Testing");
        stats = ScriptableObject.CreateInstance<AttributeSetDefinition>();
        stats.InitialAttributes = new List<AttributeSetDefinition.BaseAttribute>
        {
            new AttributeSetDefinition.BaseAttribute { Attribute = EAttributeType.Health,   BaseValue = 10000f },
            new AttributeSetDefinition.BaseAttribute { Attribute = EAttributeType.MovSpeed, BaseValue = 0f },
            new AttributeSetDefinition.BaseAttribute { Attribute = EAttributeType.Def,      BaseValue = 0f },
        };
        AssetDatabase.CreateAsset(stats, StatsPath);
        log.Append("ASDef_TrainingDummy creado (10 000 de vida). ");
        return stats;
    }

    // --- 2. El prefab ---
    private static GameObject EnsurePrefab(AttributeSetDefinition stats, StringBuilder log)
    {
        var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (existing != null) return existing;
        if (!AssetDatabase.IsValidFolder("Assets/Prefabs/Testing")) AssetDatabase.CreateFolder("Assets/Prefabs", "Testing");

        var root = new GameObject("TrainingDummy");
        try
        {
            root.layer = CharacterLayer;
            var asc = root.AddComponent<AbilitySystemComponent>();
            asc.CharacterRoleDefinition = stats;
            asc.TeamID = 4;
            var col = root.AddComponent<CapsuleCollider>();
            col.center = new Vector3(0f, 1f, 0f); col.height = 2f; col.radius = 0.45f;
            var dummy = root.AddComponent<TrainingDummy>();

            Material skin = AssetDatabase.LoadAssetAtPath<Material>("Assets/Art/Materials/Dummy Color.mat");
            Piece(root.transform, PrimitiveType.Capsule, "Body", new Vector3(0f, 1f, 0f), new Vector3(0.9f, 1f, 0.9f), skin);
            Piece(root.transform, PrimitiveType.Cylinder, "Base", new Vector3(0f, 0.05f, 0f), new Vector3(1.1f, 0.05f, 1.1f), skin);

            // El cartel: un canvas en el mundo, arriba de la cabeza.
            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Canvas));
            var pr = (RectTransform)panel.transform;
            pr.SetParent(root.transform, false);
            pr.localPosition = new Vector3(0f, 2.9f, 0f);
            pr.sizeDelta = new Vector2(460f, 260f);
            pr.localScale = Vector3.one * 0.01f;
            panel.GetComponent<Canvas>().renderMode = RenderMode.WorldSpace;

            dummy.Panel = pr;
            dummy.TitleText = Text(pr, "Title", 34f, FontStyles.Bold, 0f, 44f);
            dummy.StatsText = Text(pr, "Stats", 22f, FontStyles.Normal, 46f, 130f);
            dummy.EffectsText = Text(pr, "Effects", 20f, FontStyles.Normal, 178f, 82f);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            log.Append("Prefab TrainingDummy creado. ");
        }
        finally { Object.DestroyImmediate(root); }
        return AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
    }

    private static void Piece(Transform parent, PrimitiveType type, string name, Vector3 pos, Vector3 scale, Material mat)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.name = name;
        Object.DestroyImmediate(go.GetComponent<Collider>());   // el collider es el de la raíz
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localScale = scale;
        go.layer = parent.gameObject.layer;
        if (mat != null) go.GetComponent<Renderer>().sharedMaterial = mat;
    }

    // Un texto del cartel, de arriba hacia abajo: 'top' px desde el borde de arriba y 'height' de alto.
    private static TMP_Text Text(RectTransform parent, string name, float size, FontStyles style, float top, float height)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -top); rt.sizeDelta = new Vector2(0f, height);
        var t = go.GetComponent<TextMeshProUGUI>();
        t.fontSize = size; t.fontStyle = style; t.alignment = TextAlignmentOptions.Bottom;
        t.textWrappingMode = TextWrappingModes.Normal; t.richText = true; t.raycastTarget = false;
        t.outlineWidth = 0.15f; t.outlineColor = new Color32(0, 0, 0, 200);
        if (name == "Title") t.alignment = TextAlignmentOptions.Center;
        if (name == "Effects") t.alignment = TextAlignmentOptions.Top;
        t.text = name;
        return t;
    }

    // --- 3. La escena ---
    private struct Spot { public string Title; public Vector3 Pos; public float Armor, Resistance, Shield, RestHealth; public bool Ally; }

    private static void BuildScene(GameObject prefab, StringBuilder log)
    {
        if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) != null) { log.Append("La escena Test_Abilities ya estaba. "); return; }
        if (!AssetDatabase.CopyAsset(SourceScene, ScenePath)) { log.Append("¡Ojo! No pude copiar Test_Network. "); return; }

        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
        try
        {
            // El piso de Test_Network: plataformas a 1 m de alto, de −36 a 12 en X y en Z; el jugador aparece en (10, −33).
            const float y = 1f;
            var spots = new List<Spot>
            {
                new Spot { Title = "Muñeco",                         Pos = new Vector3(-26f, y, -16f), RestHealth = 1f },
                new Spot { Title = "Blindado (armadura 10)",         Pos = new Vector3(-19f, y, -16f), Armor = 10f, RestHealth = 1f },
                new Spot { Title = "Resistente (50 %)",              Pos = new Vector3(-12f, y, -16f), Resistance = 0.5f, RestHealth = 1f },
                new Spot { Title = "Con escudo (300)",               Pos = new Vector3(-5f, y, -16f), Shield = 300f, RestHealth = 1f },
                new Spot { Title = "Aliado herido",                  Pos = new Vector3(2f, y, -16f), Ally = true, RestHealth = 0.5f },
                new Spot { Title = "Grupo (áreas)",                  Pos = new Vector3(-14f, y, -4f), RestHealth = 1f },
                new Spot { Title = "Grupo (áreas)",                  Pos = new Vector3(-12f, y, -2.5f), RestHealth = 1f },
                new Spot { Title = "Grupo (áreas)",                  Pos = new Vector3(-10f, y, -4f), RestHealth = 1f },
            };

            var group = new GameObject("TrainingGround");
            SceneManager.MoveGameObjectToScene(group, scene);
            Vector3 lookAt = new Vector3(10f, y, -33f);
            foreach (Spot s in spots)
            {
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                go.transform.SetParent(group.transform, true);
                go.transform.position = s.Pos;
                Vector3 dir = lookAt - s.Pos; dir.y = 0f;
                go.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
                go.name = "Dummy_" + s.Title.Split(' ')[0];
                var d = go.GetComponent<TrainingDummy>();
                d.Title = s.Title; d.Armor = s.Armor; d.Resistance = s.Resistance; d.Shield = s.Shield;
                d.AllyOfLocalPlayer = s.Ally; d.RestHealth = s.RestHealth;
                EditorUtility.SetDirty(d);
            }

            // Una copia de escena: los SceneId de FishNet se rehacen TODOS (si no, chocan con los de Test_Network).
            MethodInfo create = typeof(NetworkObject).GetMethod("CreateSceneId", BindingFlags.Static | BindingFlags.NonPublic, null,
                                                                new[] { typeof(Scene), typeof(bool), typeof(int).MakeByRefType() }, null);
            if (create != null)
            {
                object[] args = { scene, true, 0 };
                create.Invoke(null, args);
                log.Append($"SceneId rehechos ({args[2]}). ");
            }
            else log.Append("¡Ojo! No encontré NetworkObject.CreateSceneId: correr Fish-Networking ▸ Reserialize NetworkObjects. ");

            EditorSceneManager.SaveScene(scene);
            log.Append($"Escena Test_Abilities con {spots.Count} muñecos. ");
        }
        finally
        {
            EditorSceneManager.CloseScene(scene, true);
        }
    }
}
