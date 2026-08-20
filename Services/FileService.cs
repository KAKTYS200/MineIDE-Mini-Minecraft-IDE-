using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MineIDE.Models;

namespace MineIDE.Services;

public class FileService
{
    private static FileService? _instance;
    public static FileService Instance => _instance ??= new FileService();

    public FileNode BuildTree(string rootPath, bool lazyDirectories = false)
    {
        var root = new FileNode
        {
            Name = Path.GetFileName(rootPath) == "" ? rootPath : Path.GetFileName(rootPath),
            FullPath = rootPath,
            IsDirectory = true,
            Icon = "\uE8B7"
        };

        try
        {
            foreach (var dir in Directory.GetDirectories(rootPath))
            {
                var child = new FileNode
                {
                    Name = Path.GetFileName(dir),
                    FullPath = dir,
                    IsDirectory = true,
                    Icon = "\uE8B7",
                    Parent = root
                };
                try
                {
                    foreach (var sub in Directory.GetDirectories(dir))
                    {
                        child.Children.Add(new FileNode
                        {
                            Name = Path.GetFileName(sub),
                            FullPath = sub,
                            IsDirectory = true,
                            Icon = "\uE8B7",
                            Parent = child
                        });
                    }
                    foreach (var file in Directory.GetFiles(dir))
                    {
                        child.Children.Add(BuildFileNode(file, child));
                    }
                }
                catch { }
                root.Children.Add(child);
            }

            foreach (var file in Directory.GetFiles(rootPath))
            {
                root.Children.Add(BuildFileNode(file, root));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FileService.BuildTree error: {ex.Message}");
        }

        return root;
    }

    private FileNode BuildFileNode(string path, FileNode? parent)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var node = new FileNode
        {
            Name = Path.GetFileName(path),
            FullPath = path,
            IsDirectory = false,
            Parent = parent,
            Size = SafeFileLen(path),
            Icon = IconForExtension(ext),
            Language = LanguageForExtension(ext)
        };
        return node;
    }

    public static string IconForExtension(string ext) => ext switch
    {
        ".java" => "\uE9E9",
        ".kt" => "\uE9E9",
        ".json" => "\uE93C",
        ".xml" => "\uE8A5",
        ".toml" => "\uE8A5",
        ".gradle" => "\uE8A5",
        ".kts" => "\uE8A5",
        ".png" => "\uE8B9",
        ".jpg" or ".jpeg" => "\uE8B9",
        ".mcmeta" => "\uE8A5",
        ".txt" or ".md" => "\uE8A5",
        ".properties" => "\uE8A5",
        ".js" or ".ts" => "\uE9E9",
        ".cs" => "\uE9E9",
        ".py" => "\uE9E9",
        _ => "\uE8A5"
    };

    public static string LanguageForExtension(string ext) => ext switch
    {
        ".java" => "java",
        ".kt" => "kotlin",
        ".json" => "json",
        ".toml" => "ini",
        ".gradle" or ".kts" => "groovy",
        ".xml" => "xml",
        ".png" or ".jpg" or ".jpeg" => "image",
        ".mcmeta" => "json",
        ".properties" => "ini",
        ".md" => "markdown",
        ".cs" => "csharp",
        ".js" => "javascript",
        ".ts" => "typescript",
        ".py" => "python",
        _ => "plaintext"
    };

    private static long SafeFileLen(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    public string ReadFile(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return ""; }
    }

    public void WriteFile(string path, string content)
    {
        try
        {
            File.WriteAllText(path, content);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WriteFile error: {ex.Message}");
        }
    }

    public string CreateSampleProject(string rootPath, string name)
    {
        // Build a Forge-style skeleton with main mod class, mods.toml, pack.mcmeta, build.gradle
        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(Path.Combine(rootPath, "src", "main", "java", "com", "example", name.ToLowerInvariant()));
        Directory.CreateDirectory(Path.Combine(rootPath, "src", "main", "resources"));
        Directory.CreateDirectory(Path.Combine(rootPath, "src", "main", "resources", "assets", name.ToLowerInvariant(), "textures"));
        Directory.CreateDirectory(Path.Combine(rootPath, "src", "main", "resources", "assets", name.ToLowerInvariant(), "models"));
        Directory.CreateDirectory(Path.Combine(rootPath, "runs"));

        var pkg = $"com.example.{name.ToLowerInvariant()}";
        var mainClass = $@"package {pkg};

import net.minecraftforge.eventbus.api.IEventBus;
import net.minecraftforge.common.MinecraftForge;
import net.minecraftforge.fml.common.Mod;
import net.minecraftforge.fml.javafmlmod.FMLJavaModLoadingContext;

@Mod(""{name}"")
public class {Capitalize(name)}
{{
    public {Capitalize(name)}()
    {{
        IEventBus bus = FMLJavaModLoadingContext.get().getModEventBus();
        MinecraftForge.EVENT_BUS.register(this);
    }}
}}
";

        File.WriteAllText(Path.Combine(rootPath, "src", "main", "java", "com", "example", name.ToLowerInvariant(), Capitalize(name) + ".java"), mainClass);
        Directory.CreateDirectory(Path.Combine(rootPath, "src", "main", "resources", "META-INF"));
        File.WriteAllText(Path.Combine(rootPath, "src", "main", "resources", "META-INF", "mods.toml"),
            @"modLoader=""javafml""
loaderVersion=""[47,)""
license=""MIT""
[[mods]]
modId=""" + name + @"""
version=""1.0.0""
displayName=""" + Capitalize(name) + @"""
description=""Sample mod""
authors=""You""
");

        File.WriteAllText(Path.Combine(rootPath, "build.gradle"),
            @"plugins {
    id 'java'
    id 'eclipse'
    id 'idea'
    id 'maven-publish'
    id 'net.minecraftforge.gradle' version '5.+'
}

version = '1.0.0'
group = 'com.example'

java {
    sourceCompatibility = JavaVersion.VERSION_17
    targetCompatibility = JavaVersion.VERSION_17
}

repositories {
    mavenCentral()
}

dependencies {
    minecraft 'net.minecraftforge:forge:1.20.1-47.2.0'
}
");

        File.WriteAllText(Path.Combine(rootPath, "gradle.properties"),
            @"org.gradle.jvmargs=-Xmx2G
org.gradle.daemon=false
");

        File.WriteAllText(Path.Combine(rootPath, "settings.gradle"),
            @"rootProject.name = '" + name + @"'
");

        File.WriteAllText(Path.Combine(rootPath, "README.md"),
            "# " + Capitalize(name) + "\n\nA Minecraft Forge mod.\n");

        return rootPath;
    }

    // ---------- project scaffolding (Explorer “+”: “Создать мод” / “Создать датапак”) ----------

    /// <summary>Creates a ready-made Forge 1.20.1 mod project: gradle files + src/main (java, resources, assets).</summary>
    public string CreateModProject(string rootPath, string name)
    {
        var modId = ToModId(name);
        var className = ToPascalCase(modId);
        var pkg = "com.example." + modId;
        var assets = Path.Combine(rootPath, "src", "main", "resources", "assets", modId);

        Directory.CreateDirectory(Path.Combine(rootPath, "src", "main", "java", "com", "example", modId));
        Directory.CreateDirectory(Path.Combine(rootPath, "src", "main", "resources", "META-INF"));
        Directory.CreateDirectory(Path.Combine(assets, "blockstates"));
        Directory.CreateDirectory(Path.Combine(assets, "lang"));
        Directory.CreateDirectory(Path.Combine(assets, "models", "block"));
        Directory.CreateDirectory(Path.Combine(assets, "models", "item"));
        Directory.CreateDirectory(Path.Combine(assets, "textures", "block"));
        Directory.CreateDirectory(Path.Combine(assets, "textures", "item"));
        Directory.CreateDirectory(Path.Combine(rootPath, "runs"));

        File.WriteAllText(Path.Combine(rootPath, "src", "main", "java", "com", "example", modId, className + ".java"),
            ModMainClass(pkg, modId, className, name));
        File.WriteAllText(Path.Combine(rootPath, "src", "main", "resources", "META-INF", "mods.toml"),
            ModsToml(modId, name));
        File.WriteAllText(Path.Combine(rootPath, "src", "main", "resources", "pack.mcmeta"),
            "{\r\n  \"pack\": {\r\n    \"description\": \"" + name + " resources\",\r\n    \"pack_format\": 15\r\n  }\r\n}\r\n");
        File.WriteAllText(Path.Combine(assets, "lang", "en_us.json"),
            "{\r\n  \"itemGroup." + modId + "\": \"" + name + "\"\r\n}\r\n");
        File.WriteAllText(Path.Combine(rootPath, "build.gradle"), ModBuildGradle(modId));
        File.WriteAllText(Path.Combine(rootPath, "gradle.properties"), ModGradleProperties(modId, name));
        File.WriteAllText(Path.Combine(rootPath, "settings.gradle"), "rootProject.name = '" + name + "'\n");
        File.WriteAllText(Path.Combine(rootPath, "README.md"), "# " + name + "\n\nA Minecraft Forge mod created in Mine IDE.\n");

        return rootPath;
    }

    /// <summary>Creates a ready-made Minecraft data pack (1.20.1): pack.mcmeta + data/&lt;namespace&gt;.</summary>
    public string CreateDatapackProject(string rootPath, string name)
    {
        var ns = ToModId(name);
        var data = Path.Combine(rootPath, "data", ns);

        Directory.CreateDirectory(Path.Combine(data, "function"));
        Directory.CreateDirectory(Path.Combine(data, "advancement"));
        Directory.CreateDirectory(Path.Combine(data, "recipe"));
        Directory.CreateDirectory(Path.Combine(data, "loot_table"));
        Directory.CreateDirectory(Path.Combine(data, "tags", "function"));

        File.WriteAllText(Path.Combine(rootPath, "pack.mcmeta"),
            "{\r\n  \"pack\": {\r\n    \"description\": \"" + name + " data pack\",\r\n    \"pack_format\": 15\r\n  }\r\n}\r\n");
        File.WriteAllText(Path.Combine(data, "function", "hello.mcfunction"),
            "say Hello from " + name + "!\r\n");
        File.WriteAllText(Path.Combine(rootPath, "README.md"), "# " + name + "\n\nA Minecraft data pack created in Mine IDE.\n");

        return rootPath;
    }

    /// <summary>Sanitizes a name for use as a folder name.</summary>
    public string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(s) ? "mod" : s;
    }

    private static string ToModId(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in name.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        var s = sb.ToString().Trim('_');
        return string.IsNullOrEmpty(s) ? "mod" : s;
    }

    private static string ToPascalCase(string modId)
    {
        var sb = new System.Text.StringBuilder();
        var cap = true;
        foreach (var c in modId)
        {
            if (c == '_') { cap = true; continue; }
            sb.Append(cap ? char.ToUpper(c) : c);
            cap = false;
        }
        return sb.Length == 0 ? "Mod" : sb.ToString();
    }

    private static string ModMainClass(string pkg, string modId, string className, string name)
        => $"package {pkg};\r\n\r\n" +
           "import net.minecraftforge.common.MinecraftForge;\r\n" +
           "import net.minecraftforge.eventbus.api.IEventBus;\r\n" +
           "import net.minecraftforge.fml.common.Mod;\r\n" +
           "import net.minecraftforge.fml.javafmlmod.FMLJavaModLoadingContext;\r\n" +
           "import org.apache.logging.log4j.LogManager;\r\n" +
           "import org.apache.logging.log4j.Logger;\r\n\r\n" +
           $"@Mod(\"{modId}\")\r\n" +
           $"public class {className}\r\n" +
           "{\r\n" +
           $"    public static final String MODID = \"{modId}\";\r\n" +
           "    private static final Logger LOGGER = LogManager.getLogger();\r\n\r\n" +
           $"    public {className}()\r\n" +
           "    {\r\n" +
           "        IEventBus bus = FMLJavaModLoadingContext.get().getModEventBus();\r\n" +
           "        MinecraftForge.EVENT_BUS.register(this);\r\n" +
           $"        LOGGER.info(\"{name} загружен!\");\r\n" +
           "    }\r\n" +
           "}\r\n";

    private static string ModsToml(string modId, string name)
        => "modLoader=\"javafml\"\r\n" +
           "loaderVersion=\"[47,)\"\r\n" +
           "license=\"All rights reserved\"\r\n" +
           "\r\n" +
           "[[mods]]\r\n" +
           $"modId=\"{modId}\"\r\n" +
           "version=\"1.0.0\"\r\n" +
           $"displayName=\"{name}\"\r\n" +
           "authors=\"You\"\r\n" +
           $"description='''A {name} mod.'''\r\n" +
           "\r\n" +
           $"[[dependencies.{modId}]]\r\n" +
           "    modId=\"forge\"\r\n" +
           "    mandatory=true\r\n" +
           "    versionRange=\"[47,)\"\r\n" +
           "    ordering=\"NONE\"\r\n" +
           "    side=\"BOTH\"\r\n" +
           "\r\n" +
           $"[[dependencies.{modId}]]\r\n" +
           "    modId=\"minecraft\"\r\n" +
           "    mandatory=true\r\n" +
           "    versionRange=\"[1.20.1,1.21)\"\r\n" +
           "    ordering=\"NONE\"\r\n" +
           "    side=\"BOTH\"\r\n";

    private static string ModBuildGradle(string modId)
        => "plugins {\r\n" +
           "    id 'eclipse'\r\n" +
           "    id 'idea'\r\n" +
           "    id 'maven-publish'\r\n" +
           "    id 'net.minecraftforge.gradle' version '[6.0,6.2)'\r\n" +
           "}\r\n\r\n" +
           "version = mod_version\r\n" +
           "group = mod_group_id\r\n\r\n" +
           "base {\r\n" +
           $"    archivesName = mod_id\r\n" +
           "}\r\n\r\n" +
           "java.toolchain.languageVersion = JavaLanguageVersion.of(17)\r\n\r\n" +
           "minecraft {\r\n" +
           "    mappings channel: 'official', version: minecraft_version\r\n" +
           "    copyIdeResources = true\r\n" +
           "\r\n" +
           "    runs {\r\n" +
           "        configureEach {\r\n" +
           "            workingDirectory project.file('run')\r\n" +
           "            property 'forge.logging.markers', 'REGISTRIES'\r\n" +
           "            property 'forge.logging.console.level', 'debug'\r\n" +
           "            mods {\r\n" +
           $"                \"{modId}\" {{ source sourceSets.main }}\r\n" +
           "            }\r\n" +
           "        }\r\n" +
           "        client {\r\n" +
           $"            property 'forge.enabledGameTestNamespaces', mod_id\r\n" +
           "        }\r\n" +
           "        server {\r\n" +
           $"            property 'forge.enabledGameTestNamespaces', mod_id\r\n" +
           "        }\r\n" +
           "        data {\r\n" +
           "            workingDirectory project.file('run-data')\r\n" +
           "            args '--mod', mod_id, '--all', '--output', file('src/generated/resources/'), '--existing', file('src/main/resources/')\r\n" +
           "        }\r\n" +
           "    }\r\n" +
           "}\r\n\r\n" +
           "sourceSets.main.resources { srcDir 'src/generated/resources' }\r\n\r\n" +
           "repositories {\r\n" +
           "    mavenCentral()\r\n" +
           "}\r\n\r\n" +
           "dependencies {\r\n" +
           "    minecraft \"net.minecraftforge:forge:${minecraft_version}-${forge_version}\"\r\n" +
           "}\r\n";

    private static string ModGradleProperties(string modId, string name)
        => "org.gradle.jvmargs=-Xmx2G\r\n" +
           "org.gradle.daemon=false\r\n" +
           "\r\n" +
           "minecraft_version=1.20.1\r\n" +
           "minecraft_version_range=[1.20.1,1.21)\r\n" +
           "forge_version=47.2.0\r\n" +
           "forge_version_range=[47,)\r\n" +
           "loader_version_range=[47,)\r\n" +
           $"mod_id={modId}\r\n" +
           $"mod_name={name}\r\n" +
           "mod_license=All rights reserved\r\n" +
           "mod_version=1.0.0\r\n" +
           "mod_authors=You\r\n" +
           $"mod_group_id=com.example.{modId}\r\n";

    private string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);
}
