using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MineIDE.RecipeEditor.Models;

namespace MineIDE.RecipeEditor.Services;

/// <summary>
/// Builds a valid Minecraft recipe JSON from a <see cref="RecipeDocument"/> and validates it.
/// Kept free of any UI/WPF dependency so it can be reused by other editors.
/// </summary>
public static class RecipeJsonGenerator
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Generate(RecipeDocument d)
    {
        JsonObject root = d.Kind switch
        {
            RecipeKind.Shaped => BuildShaped(d),
            RecipeKind.Shapeless => BuildShapeless(d),
            RecipeKind.Smelting => BuildFurnace("minecraft:smelting", d, 200),
            RecipeKind.Blasting => BuildFurnace("minecraft:blasting", d, 100),
            RecipeKind.Smoking => BuildFurnace("minecraft:smoking", d, 100),
            RecipeKind.Campfire => BuildFurnace("minecraft:campfire_cooking", d, 600),
            RecipeKind.Stonecutting => BuildStonecutting(d),
            RecipeKind.Smithing => BuildSmithing(d),
            _ => new JsonObject { ["type"] = "minecraft:crafting_shaped" }
        };
        return root.ToJsonString(Indented);
    }

    /// <summary>Returns human-readable error messages (Russian, matching the IDE UI).</summary>
    public static List<string> Validate(RecipeDocument d)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(d.Result.Id))
            errors.Add("Результат: укажите предмет (item id).");

        switch (d.Kind)
        {
            case RecipeKind.Shaped:
                if (!d.Grid.Any(c => !string.IsNullOrWhiteSpace(c.Id)))
                    errors.Add("Shaped: заполните хотя бы одну ячейку сетки.");
                break;

            case RecipeKind.Shapeless:
                if (!d.Grid.Any(c => !string.IsNullOrWhiteSpace(c.Id)))
                    errors.Add("Shapeless: добавьте хотя бы один ингредиент.");
                break;

            case RecipeKind.Smelting:
            case RecipeKind.Blasting:
            case RecipeKind.Smoking:
            case RecipeKind.Campfire:
            case RecipeKind.Stonecutting:
                if (string.IsNullOrWhiteSpace(d.Ingredient.Id))
                    errors.Add("Укажите ингредиент (item id).");
                break;

            case RecipeKind.Smithing:
                if (string.IsNullOrWhiteSpace(d.Template.Id)) errors.Add("Smithing: укажите template (шаблон).");
                if (string.IsNullOrWhiteSpace(d.Base.Id)) errors.Add("Smithing: укажите base (основу).");
                if (string.IsNullOrWhiteSpace(d.Addition.Id)) errors.Add("Smithing: укажите addition (добавку).");
                break;
        }

        return errors;
    }

    private static JsonObject ItemNode(RecipeItem i)
    {
        var o = new JsonObject { ["item"] = i.Id };
        if (i.Count > 1) o["count"] = i.Count;
        if (!string.IsNullOrWhiteSpace(i.Nbt)) o["nbt"] = i.Nbt; // raw SNBT string
        return o;
    }

    private static JsonObject BuildShaped(RecipeDocument d)
    {
        var obj = new JsonObject { ["type"] = "minecraft:crafting_shaped" };

        // Assign a key character to each unique item definition, ' ' for empty cells.
        var grid = new char[9];
        var charToItem = new Dictionary<char, RecipeItem>();
        var sigToChar = new Dictionary<string, char>();
        char next = 'A';

        for (int i = 0; i < 9; i++)
        {
            var cell = d.Grid[i];
            if (string.IsNullOrWhiteSpace(cell.Id)) { grid[i] = ' '; continue; }
            string sig = cell.Id + "\u0001" + cell.Count + "\u0001" + cell.Nbt;
            if (!sigToChar.TryGetValue(sig, out var ch))
            {
                ch = next++;
                sigToChar[sig] = ch;
                charToItem[ch] = cell;
            }
            grid[i] = ch;
        }

        var pattern = new List<string>();
        for (int r = 0; r < 3; r++)
        {
            var row = new string(grid.Skip(r * 3).Take(3).ToArray()).TrimEnd();
            if (row.Length > 0) pattern.Add(row);
        }
        while (pattern.Count > 0 && pattern[^1].Length == 0) pattern.RemoveAt(pattern.Count - 1);

        var patternArr = new JsonArray();
        foreach (var p in pattern) patternArr.Add(JsonValue.Create(p));
        obj["pattern"] = patternArr;

        var keyObj = new JsonObject();
        foreach (var kv in charToItem)
            keyObj[kv.Key.ToString()] = ItemNode(kv.Value);
        obj["key"] = keyObj;

        obj["result"] = ItemNode(d.Result);
        if (!string.IsNullOrWhiteSpace(d.Group)) obj["group"] = d.Group;
        return obj;
    }

    private static JsonObject BuildShapeless(RecipeDocument d)
    {
        var obj = new JsonObject { ["type"] = "minecraft:crafting_shapeless" };

        var ingredients = new JsonArray();
        foreach (var cell in d.Grid)
            if (!string.IsNullOrWhiteSpace(cell.Id))
                ingredients.Add(ItemNode(cell));
        obj["ingredients"] = ingredients;

        obj["result"] = ItemNode(d.Result);
        if (!string.IsNullOrWhiteSpace(d.Group)) obj["group"] = d.Group;
        return obj;
    }

    private static JsonObject BuildFurnace(string type, RecipeDocument d, int defaultTime)
    {
        var obj = new JsonObject { ["type"] = type };
        obj["ingredient"] = ItemNode(d.Ingredient);
        obj["result"] = ItemNode(d.Result);
        if (d.Experience > 0) obj["experience"] = d.Experience;
        obj["cookingtime"] = d.CookingTime > 0 ? d.CookingTime : defaultTime;
        return obj;
    }

    private static JsonObject BuildStonecutting(RecipeDocument d)
    {
        var obj = new JsonObject { ["type"] = "minecraft:stonecutting" };
        obj["ingredient"] = ItemNode(d.Ingredient);
        obj["result"] = ItemNode(d.Result);
        return obj;
    }

    private static JsonObject BuildSmithing(RecipeDocument d)
    {
        var obj = new JsonObject { ["type"] = "minecraft:smithing_transform" };
        obj["template"] = ItemNode(d.Template);
        obj["base"] = ItemNode(d.Base);
        obj["addition"] = ItemNode(d.Addition);
        obj["result"] = ItemNode(d.Result);
        return obj;
    }
}
