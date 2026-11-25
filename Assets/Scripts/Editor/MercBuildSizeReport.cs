using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

// ============================================================
// MercBuildSizeReport  (qué pesa en la build — 25 de noviembre de 2026)
//
// De la B9 a la B10 el archivo grande de la build (sharedassets0.assets.resS) pasó de 87 a 194 MB y la build dejó de
// entrar en GitHub (tope de 100 MB por archivo). Unity no dice qué lo llena (el reporte de la build solo trae el total),
// así que esto lo calcula: recorre lo que usan las escenas de la build y TODO lo que está en una carpeta Resources (eso
// entra siempre, se use o no) y mide cada textura (lo que ocupa ya comprimida, como queda en la build), cada malla y
// cada audio.
//
// Menú: Mercenarios ▸ Build ▸ Qué pesa en la build. Escribe el informe en la consola y en Logs/BuildSizeReport.txt.
// Desde el CLI: MercBuildSizeReport.Report(). Solo LEE: no cambia ningún asset.
//
// Cómo leerlo:
//   · "Entra solo por estar en Resources": lo que ninguna escena usa. Una carpeta Resources de la DEMO de un pack
//     comprado mete su demo entera en la build: renombrarla (p. ej. "Resources_Demo") la saca, si nada del juego la
//     carga por nombre.
//   · Una textura más grande que 2048 (el cielo): bajar su "Max Size" a 2048 la deja en un cuarto.
//   · Las mallas son lo que son: se achican con menos triángulos (las de Blender, en sus recetas).
// ============================================================
public static class MercBuildSizeReport
{
    private const int TopCount = 50;

    [MenuItem("Mercenarios/Build/Qué pesa en la build")]
    public static void ReportMenu()
    {
        string text = Report();
        Debug.Log(text);
    }

    private class Row
    {
        public long   Bytes;
        public string Kind;   // TEX / MESH / AUD
        public string Path;
        public string Info;
        public string Hint;
    }

    public static string Report()
    {
        // Lo que entra: las escenas prendidas de la build y lo que está en una carpeta Resources (fuera de Editor).
        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        var resourceRoots = new List<string>();
        foreach (string guid in AssetDatabase.FindAssets("", new[] { "Assets" }))
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (AssetDatabase.IsValidFolder(p)) continue;
            if (!p.Contains("/Resources/") || p.Contains("/Editor/")) continue;
            resourceRoots.Add(p);
        }

        var fromScenes = new HashSet<string>(AssetDatabase.GetDependencies(scenes, true));
        var all = new HashSet<string>(fromScenes);
        foreach (string d in AssetDatabase.GetDependencies(resourceRoots.ToArray(), true)) all.Add(d);

        MethodInfo storage = typeof(Editor).Assembly.GetType("UnityEditor.TextureUtil")?
            .GetMethod("GetStorageMemorySizeLong", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        var rows = new List<Row>();
        foreach (string path in all)
        {
            Row r = Measure(path, storage);
            if (r != null) rows.Add(r);
        }
        rows.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));

        var sb = new StringBuilder();
        sb.AppendLine("[Qué pesa en la build] " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        sb.AppendLine("Escenas: " + (scenes.Length > 0 ? string.Join(", ", scenes) : "(ninguna prendida)"));
        sb.AppendLine($"Total medido: {MB(rows.Sum(r => r.Bytes))}  (texturas {MB(rows.Where(r => r.Kind == "TEX").Sum(r => r.Bytes))}, " +
                      $"mallas {MB(rows.Where(r => r.Kind == "MESH").Sum(r => r.Bytes))}, " +
                      $"audio {MB(rows.Where(r => r.Kind == "AUD").Sum(r => r.Bytes))}). " +
                      "El motor y el código van aparte.");
        sb.AppendLine("(Las texturas, ya comprimidas como quedan en la build; mallas y audio, aproximado.)");

        // Lo que ninguna escena usa: entra solo por estar en una carpeta Resources. Agrupado por esa carpeta.
        sb.AppendLine();
        sb.AppendLine("--- Entra SOLO por estar en una carpeta Resources (ninguna escena lo usa) ---");
        var onlyRes = rows.Where(r => !fromScenes.Contains(r.Path)).ToList();
        if (onlyRes.Count == 0) sb.AppendLine("Nada.");
        foreach (var group in onlyRes.GroupBy(r => ResourcesFolderOf(r.Path, resourceRoots)).OrderByDescending(g => g.Sum(r => r.Bytes)))
        {
            sb.AppendLine($"{MB(group.Sum(r => r.Bytes)),9}  {group.Key}  ({group.Count()} assets)");
            foreach (Row r in group.Take(5)) sb.AppendLine($"           {MB(r.Bytes),9}  {r.Kind,-4} {r.Path}");
        }

        sb.AppendLine();
        sb.AppendLine($"--- Lo más pesado ({TopCount}) ---");
        foreach (Row r in rows.Take(TopCount))
        {
            sb.Append($"{MB(r.Bytes),9}  {r.Kind,-4} {r.Path}");
            if (!string.IsNullOrEmpty(r.Info)) sb.Append($"  [{r.Info}]");
            if (!fromScenes.Contains(r.Path)) sb.Append("  (solo Resources)");
            if (!string.IsNullOrEmpty(r.Hint)) sb.Append($"  → {r.Hint}");
            sb.AppendLine();
        }

        string text = sb.ToString();
        try
        {
            Directory.CreateDirectory("Logs");
            File.WriteAllText(Path.Combine("Logs", "BuildSizeReport.txt"), text, new UTF8Encoding(false));
        }
        catch (System.Exception e) { Debug.LogWarning("[Qué pesa en la build] No pude escribir Logs/BuildSizeReport.txt: " + e.Message); }
        return text;
    }

    private static Row Measure(string path, MethodInfo storage)
    {
        System.Type type = AssetDatabase.GetMainAssetTypeAtPath(path);
        if (type == null) return null;

        if (typeof(Texture).IsAssignableFrom(type))
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture>(path);
            if (tex == null) return null;
            long bytes = storage != null ? (long)storage.Invoke(null, new object[] { tex }) : Profiler.GetRuntimeMemorySizeLong(tex);
            var row = new Row { Bytes = bytes, Kind = "TEX", Path = path, Info = $"{tex.width}x{tex.height}" };
            if (AssetImporter.GetAtPath(path) is TextureImporter imp)
            {
                row.Info += $", Max Size {imp.maxTextureSize}, {imp.textureCompression}{(imp.crunchedCompression ? " crunch" : "")}";
                if (imp.textureCompression == TextureImporterCompression.Uncompressed) row.Hint = "sin comprimir: ponerle compresión";
                else if (Mathf.Max(tex.width, tex.height) > 2048) row.Hint = "Max Size 2048 la deja en un cuarto";
            }
            return row;
        }

        if (type == typeof(AudioClip))
            return new Row { Bytes = new FileInfo(path).Length, Kind = "AUD", Path = path, Info = "tamaño del archivo" };

        bool model = type == typeof(Mesh) ||
                     (type == typeof(GameObject) && AssetImporter.GetAtPath(path) is ModelImporter);
        if (!model) return null;

        long meshBytes = 0;
        int tris = 0;
        foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(path))
        {
            if (!(o is Mesh m)) continue;
            meshBytes += Profiler.GetRuntimeMemorySizeLong(m);
            for (int s = 0; s < m.subMeshCount; s++) tris += (int)(m.GetIndexCount(s) / 3);
        }
        if (meshBytes == 0) return null;
        return new Row { Bytes = meshBytes, Kind = "MESH", Path = path, Info = $"{tris:N0} triángulos" };
    }

    // La carpeta Resources de la que cuelga 'path' (o la de quien lo usa, si el asset está fuera de una).
    private static string ResourcesFolderOf(string path, List<string> resourceRoots)
    {
        int i = path.IndexOf("/Resources/");
        if (i >= 0) return path.Substring(0, i + "/Resources".Length);
        foreach (string root in resourceRoots)
        {
            if (System.Array.IndexOf(AssetDatabase.GetDependencies(root, true), path) < 0) continue;
            int j = root.IndexOf("/Resources/");
            return root.Substring(0, j + "/Resources".Length) + " (lo usa)";
        }
        return "(otra)";
    }

    private static string MB(long bytes) => (bytes / 1048576.0).ToString("0.0") + " MB";
}
