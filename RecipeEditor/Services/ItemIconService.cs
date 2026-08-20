using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MineIDE.RecipeEditor.Services;

/// <summary>
/// Resolves Minecraft item ids (e.g. "minecraft:iron_ingot") to cached 16×16
/// textures extracted from a local Minecraft client jar, and enumerates the full
/// vanilla item list.
///
/// Textures are resolved by following each item's model JSON (models/item/*.json):
/// a simple item has "textures.layer0", while a block item references a block model
/// whose faces (all/top/side/...) point at the real texture. This is what lets
/// stairs, slabs, fences, spawn eggs, etc. show a proper icon even though the
/// texture file name differs from the item id.
///
/// The module stays self-contained: it only reads the game installation and writes
/// to a cache folder under %LOCALAPPDATA%\MineIDE, never touching the MineIDE core.
/// </summary>
public sealed class ItemIconService
{
    public static ItemIconService Instance { get; } = new();

    private const string ItemPrefix = "assets/minecraft/textures/item/";
    private const string BlockPrefix = "assets/minecraft/textures/block/";
    private const string GuiPrefix = "assets/minecraft/textures/gui/container/";
    private const string AdvGuiPrefix = "assets/minecraft/textures/gui/advancements/";
    private const string ModelItemPrefix = "assets/minecraft/models/item/";
    private const string ModelBlockPrefix = "assets/minecraft/models/block/";

    private static readonly Regex FrameSuffix = new(@"^(?:bow_pulling|brush_brushing|crossbow_pulling)_\d+$", RegexOptions.Compiled);
    private static readonly Regex NumberedItem = new(@"^(?:clock|compass|light|recovery_compass)_\d+$", RegexOptions.Compiled);

    // Model files that are render states / templates of another item, not real ids.
    private static readonly HashSet<string> NonItemModels = new()
    {
        "bundle_filled", "crossbow_arrow", "crossbow_firework",
        "fishing_rod_cast", "shield_blocking", "spyglass_in_hand",
        "trident_in_hand", "trident_throwing",
        // base model templates referenced by many items:
        "generated", "handheld", "handheld_rod"
    };

    private readonly object _lock = new();
    private string? _cacheRoot;
    private string? _jarPath;
    private bool _resolved;
    private bool _extracted;
    private Dictionary<string, string> _textureMap = new(); // item name -> "item/xxx" or "block/xxx"

    /// <summary>Target Minecraft version used to prefer the matching client jar.</summary>
    public string Version { get; set; } = "1.20.1";

    /// <summary>Local cache folder the textures are extracted into (created on demand).</summary>
    public string? CacheRoot
    {
        get { EnsureJarResolved(); return _cacheRoot; }
    }

    /// <summary>Returns the cached PNG path for an item id, or null when the texture is unknown.</summary>
    public string? GetIconPath(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return null;
        EnsureExtracted();
        if (_cacheRoot == null) return null;

        var name = itemId.Contains(':') ? itemId[(itemId.IndexOf(':') + 1)..] : itemId;
        if (string.IsNullOrWhiteSpace(name)) return null;

        // 1) resolved model texture (handles stairs/slabs/spawn eggs/etc.)
        if (_textureMap.TryGetValue(name, out var rel))
        {
            var path = Path.Combine(_cacheRoot, rel + ".png");
            if (File.Exists(path)) return path;
        }

        // 2) direct fallbacks
        foreach (var candidate in Candidates(name))
            if (File.Exists(candidate))
                return candidate;

        return null;
    }

    /// <summary>Returns the cached PNG path for a GUI container texture (e.g. "crafting_table"), or null.</summary>
    public string? GetGuiIconPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        EnsureExtracted();
        if (_cacheRoot == null) return null;

        var path = Path.Combine(_cacheRoot, "gui", name + ".png");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Returns the cached PNG path for an advancements GUI texture
    /// ("window" — the advancement frame sheet, or "backgrounds/stone", ...), or null.
    /// </summary>
    public string? GetAdvancementGuiPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        EnsureJarResolved();
        if (_jarPath == null || _cacheRoot == null) return null;

        lock (_lock)
        {
            try
            {
                ExtractFolder(_jarPath, _cacheRoot, AdvGuiPrefix, "advancements");
            }
            catch { /* best effort */ }
        }

        var path = Path.Combine(_cacheRoot, "advancements", name + ".png");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Returns every vanilla item id ("minecraft:*") available in the client jar,
    /// derived from item model file names (models/item/*.json). Animated frames,
    /// armor-trim variants, item-state models and model templates are filtered out.
    /// </summary>
    public IReadOnlyList<string> GetAllItemIds()
    {
        EnsureJarResolved();
        if (_jarPath == null) return Array.Empty<string>();

        try
        {
            var ids = new List<string>();
            using var zip = ZipFile.OpenRead(_jarPath);
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.StartsWith(ModelItemPrefix, StringComparison.Ordinal)) continue;
                var name = e.FullName.Substring(ModelItemPrefix.Length);
                if (!name.EndsWith(".json", StringComparison.Ordinal)) continue;
                name = name.Substring(0, name.Length - ".json".Length);
                if (IsRealItemId(name))
                    ids.Add("minecraft:" + name);
            }
            ids.Sort(StringComparer.Ordinal);
            return ids;
        }
        catch { return Array.Empty<string>(); }
    }

    private static bool IsRealItemId(string name)
    {
        if (name.StartsWith("template_", StringComparison.Ordinal)) return false;
        if (name.EndsWith("_trim", StringComparison.Ordinal)) return false;
        if (FrameSuffix.IsMatch(name) || NumberedItem.IsMatch(name)) return false;
        return !NonItemModels.Contains(name);
    }

    private IEnumerable<string> Candidates(string name)
    {
        yield return Path.Combine(_cacheRoot!, "item", name + ".png");
        yield return Path.Combine(_cacheRoot!, "item", name + "_00.png");
        yield return Path.Combine(_cacheRoot!, "block", name + ".png");
    }

    // ---------- texture resolution from model JSONs ----------

    private void BuildTextureMap(string jar)
    {
        var map = new Dictionary<string, string>();
        try
        {
            using var zip = ZipFile.OpenRead(jar);
            var itemModels = new Dictionary<string, JsonObject?>();
            var blockModels = new Dictionary<string, JsonObject?>();

            foreach (var e in zip.Entries)
            {
                if (e.FullName.StartsWith(ModelItemPrefix, StringComparison.Ordinal) && e.FullName.EndsWith(".json", StringComparison.Ordinal))
                {
                    var name = e.FullName.Substring(ModelItemPrefix.Length);
                    name = name.Substring(0, name.Length - ".json".Length);
                    itemModels[name] = TryParse(e);
                }
                else if (e.FullName.StartsWith(ModelBlockPrefix, StringComparison.Ordinal) && e.FullName.EndsWith(".json", StringComparison.Ordinal))
                {
                    var name = e.FullName.Substring(ModelBlockPrefix.Length);
                    name = name.Substring(0, name.Length - ".json".Length);
                    blockModels[name] = TryParse(e);
                }
            }

            foreach (var (name, model) in itemModels)
            {
                if (model == null) continue;
                var tex = ResolveItemTexture(name, model, itemModels, blockModels, new HashSet<string>());
                if (!string.IsNullOrEmpty(tex))
                    map[name] = tex;
            }
        }
        catch { /* leave the map empty and fall back to direct file names */ }

        _textureMap = map;
    }

    private static JsonObject? TryParse(ZipArchiveEntry entry)
    {
        try
        {
            using var s = entry.Open();
            return JsonNode.Parse(s) as JsonObject;
        }
        catch { return null; }
    }

    private static string? ResolveItemTexture(string name, JsonObject model,
        Dictionary<string, JsonObject?> itemModels, Dictionary<string, JsonObject?> blockModels, HashSet<string> visiting)
    {
        if (!visiting.Add("item/" + name)) return null;

        if (model["textures"] is JsonObject tex)
        {
            if (tex["layer0"] is JsonValue l0 && l0.TryGetValue<string>(out var s0))
            {
                // Multi-layer items (potions/arrows/spawn eggs) draw the overlay in layer0;
                // prefer the solid base layer when present.
                if (!s0.Contains("_overlay", StringComparison.Ordinal))
                    return NormalizeTexture(s0);
                if (tex["layer1"] is JsonValue l1 && l1.TryGetValue<string>(out var s1))
                    return NormalizeTexture(s1);
                return NormalizeTexture(s0);
            }
        }

        if (model["parent"] is JsonValue p && p.TryGetValue<string>(out var parent))
        {
            if (parent.StartsWith("minecraft:block/", StringComparison.Ordinal))
            {
                var bname = parent.Substring("minecraft:block/".Length);
                if (blockModels.TryGetValue(bname, out var bm) && bm != null)
                    return ResolveBlockTexture(bname, bm, blockModels, visiting);
            }
            else if (parent.StartsWith("minecraft:item/", StringComparison.Ordinal))
            {
                var iname = parent.Substring("minecraft:item/".Length);
                if (itemModels.TryGetValue(iname, out var im) && im != null)
                    return ResolveItemTexture(iname, im, itemModels, blockModels, visiting);
            }
        }

        return null;
    }

    private static string? ResolveBlockTexture(string name, JsonObject model,
        Dictionary<string, JsonObject?> blockModels, HashSet<string> visiting)
    {
        if (!visiting.Add("block/" + name)) return null;

        if (model["textures"] is JsonObject tex)
        {
            // Prefer the most representative face, in order.
            foreach (var key in new[] { "all", "texture", "side", "top", "bottom", "end", "front", "wall", "stem", "planks", "log", "particle" })
            {
                if (tex[key] is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    var norm = NormalizeTexture(s);
                    if (norm != null) return norm;
                }
            }
            // Fallback: first non-variable texture reference.
            foreach (var (_, value) in tex)
            {
                if (value is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    var norm = NormalizeTexture(s);
                    if (norm != null) return norm;
                }
            }
        }

        if (model["parent"] is JsonValue p && p.TryGetValue<string>(out var parent) &&
            parent.StartsWith("minecraft:block/", StringComparison.Ordinal))
        {
            var pname = parent.Substring("minecraft:block/".Length);
            if (blockModels.TryGetValue(pname, out var pm) && pm != null)
                return ResolveBlockTexture(pname, pm, blockModels, visiting);
        }

        return null;
    }

    /// <summary>Strips the "minecraft:" namespace and rejects variable references (#...).</summary>
    private static string? NormalizeTexture(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.StartsWith("#", StringComparison.Ordinal)) return null;
        return s.StartsWith("minecraft:", StringComparison.Ordinal) ? s.Substring("minecraft:".Length) : s;
    }

    // ---------- jar location & extraction ----------

    /// <summary>Locates the client jar and prepares the cache path without extracting textures.</summary>
    private void EnsureJarResolved()
    {
        lock (_lock)
        {
            if (_resolved) return;
            _resolved = true;

            try
            {
                var jar = LocateClientJar();
                if (jar == null) return;

                _jarPath = jar;
                _cacheRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MineIDE", "item_icons", Sanitize(Path.GetFileNameWithoutExtension(jar)));
                Directory.CreateDirectory(_cacheRoot);
            }
            catch
            {
                _jarPath = null;
                _cacheRoot = null;
            }
        }
    }

    /// <summary>Extracts item/block/gui textures and builds the texture map (lazy, first icon request).</summary>
    private void EnsureExtracted()
    {
        EnsureJarResolved();
        if (_jarPath == null || _cacheRoot == null) return;

        lock (_lock)
        {
            if (_extracted) return;
            _extracted = true;
            try
            {
                Extract(_jarPath, _cacheRoot);
                BuildTextureMap(_jarPath);
            }
            catch { _extracted = false; }
        }
    }

    private string? LocateClientJar()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var versions = Path.Combine(appData, ".minecraft", "versions");
        if (!Directory.Exists(versions)) return null;

        var jars = new List<string>();
        foreach (var dir in Directory.GetDirectories(versions))
        {
            var dirName = Path.GetFileName(dir);
            foreach (var jar in Directory.GetFiles(dir, "*.jar"))
            {
                var jarName = Path.GetFileName(jar);
                if (dirName.Contains(Version, StringComparison.OrdinalIgnoreCase) ||
                    jarName.Contains(Version, StringComparison.OrdinalIgnoreCase))
                    jars.Insert(0, jar);
                else
                    jars.Add(jar);
            }
        }

        foreach (var jar in jars)
            if (ContainsVanillaTextures(jar))
                return jar;

        return null;
    }

    private static bool ContainsVanillaTextures(string jar)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jar);
            foreach (var e in zip.Entries)
                if (e.FullName.StartsWith(ItemPrefix, StringComparison.Ordinal))
                    return true;
        }
        catch { /* not a readable jar */ }
        return false;
    }

    /// <summary>Extracts every vanilla item + block + gui texture from the jar into the cache (skips existing files).</summary>
    private static void Extract(string jar, string root)
    {
        using var zip = ZipFile.OpenRead(jar);
        foreach (var entry in zip.Entries)
        {
            string rel;
            string sub;
            if (entry.FullName.StartsWith(ItemPrefix, StringComparison.Ordinal))
            {
                sub = "item";
                rel = entry.FullName.Substring(ItemPrefix.Length);
            }
            else if (entry.FullName.StartsWith(BlockPrefix, StringComparison.Ordinal))
            {
                sub = "block";
                rel = entry.FullName.Substring(BlockPrefix.Length);
            }
            else if (entry.FullName.StartsWith(GuiPrefix, StringComparison.Ordinal))
            {
                sub = "gui";
                rel = entry.FullName.Substring(GuiPrefix.Length);
            }
            else continue;

            if (rel.EndsWith("/", StringComparison.Ordinal) || rel.Length == 0) continue;

            var target = Path.Combine(root, sub, rel);
            if (File.Exists(target)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, true);
            }
            catch { /* skip unreadable entry */ }
        }
    }

    /// <summary>Extracts one folder prefix (e.g. gui/advancements) into the cache, skipping existing files.</summary>
    private static void ExtractFolder(string jar, string root, string prefix, string sub)
    {
        using var zip = ZipFile.OpenRead(jar);
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rel = entry.FullName.Substring(prefix.Length);
            if (rel.EndsWith("/", StringComparison.Ordinal) || rel.Length == 0) continue;

            var target = Path.Combine(root, sub, rel);
            if (File.Exists(target)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, true);
            }
            catch { /* skip unreadable entry */ }
        }
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(result) ? "vanilla" : result;
    }
}
