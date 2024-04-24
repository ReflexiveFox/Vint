using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BepuPhysics.Collidables;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.GZip;
using SharpGLTF.Schema2;
using Vint.Core.Config.MapInformation;
using Vint.Core.ECS.Components;
using Vint.Core.ECS.Components.Server;
using Vint.Core.ECS.Entities;
using Vint.Core.ECS.Templates;
using Vint.Core.Protocol.Attributes;
using Vint.Core.Utils;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Vint.Core.Config;

public static class ConfigManager {
    public static uint SeasonNumber => 1; // todo do something with this;

    private static readonly Dictionary<char, string> EnReplacements = new Dictionary<char, string>()
        {
            {'a', "[aа4]"}, // Matches both 'a' and 'а'
            {'e', "[eе3€]"}, // Matches both 'e' and 'е'
            {'o', "[oо0]"}, // Matches both 'o' and 'о'
            {'c', "[cс]"}, // Matches both 'c' and 'с'
            {'p', "[pр]"}, // Matches both 'p' and 'р'
            {'x', "[xх]"}, // Matches both 'x' and 'х'
            {'y', "[yу]"}, // Matches both 'y' and 'у'
            {'h', "[hн]"}, // Matches both 'h' and 'н'
            {'k', "[kк]"}, // Matches both 'k' and 'к'
            {'b', "[bв]"}, // Matches both 'b' and 'в'
            {'m', "[mм]"}, // Matches both 'm' and 'м'
            {'d', "[dд]"},
            {'i', "[i1]"},
            {'s', "[s$]"}
            // Add more substitutions as needed
        };

    private static readonly Dictionary<char,string> RuReplacements = new Dictionary<char, string>() {
            {'а', "[aа4]"}, // Matches both 'a' and 'а'
            {'е', "[eе3€]"}, // Matches both 'e' and 'е'
            {'о', "[oо0]"}, // Matches both 'o' and 'о'
            {'с', "[cс]"}, // Matches both 'c' and 'с'
            {'р', "[pр]"}, // Matches both 'p' and 'р'
            {'х', "[xх]"}, // Matches both 'x' and 'х'
            {'у', "[yу]"}, // Matches both 'y' and 'у'
            {'н', "[hн]"}, // Matches both 'h' and 'н'
            {'к', "[kк]"}, // Matches both 'k' and 'к'
            {'в', "[bв]"}, // Matches both 'b' and 'в'
            {'м', "[mм]"}, // Matches both 'm' and 'м'
            {'д', "[dд]"},
        };
    private static string badWordsFilePath = "badwords.txt";
    public static Dictionary<string, string>? BadWordsPatterns;
    public static FrozenSet<MapInfo> MapInfos { get; private set; } = FrozenSet<MapInfo>.Empty;
    public static FrozenDictionary<string, BlueprintChest> Blueprints { get; private set; } = FrozenDictionary<string, BlueprintChest>.Empty;
    public static FrozenDictionary<string, Triangle[]> MapNameToTriangles { get; private set; } = FrozenDictionary<string, Triangle[]>.Empty;
    public static ModulePrices ModulePrices { get; private set; }
    public static DiscordConfig Discord { get; private set; }

    public static IEnumerable<string> GlobalEntitiesTypeNames => Root.Children
        .Where(child => child.Value.Entities.Count != 0)
        .Select(child => child.Key);

    static ILogger Logger { get; } = Log.Logger.ForType(typeof(ConfigManager));
    static string ResourcesPath { get; } = Path.Combine(Directory.GetCurrentDirectory(), "Resources");

    static FrozenDictionary<string, byte[]> LocaleToConfigCache { get; set; } = FrozenDictionary<string, byte[]>.Empty;
    static ConfigNode Root { get; } = new();

    public static void InitializeBadWordsDictionary() {
        Logger.Information("Initializing bad words file");

        string filePath = Path.Combine(ResourcesPath, badWordsFilePath);
        BadWordsPatterns = LoadAndProcessWords(filePath);
        Logger.Information("Bad words file created");
    }

    /// <summary>
    /// Reads from file the list of bad words and put them into dictionary with letter variations
    /// </summary>
    /// <param name="filePath"></param>
    /// <returns>The dictionary with bad words as keys and letter variations as values</returns>
    private static Dictionary<string, string> LoadAndProcessWords(string filePath) {
        var patterns = new Dictionary<string, string>();
        
        string[] words = File.ReadAllLines(filePath);
        foreach (var word in words) {
            string pattern = Regex.Escape(word.ToLower()); // Escape to safely use in regex
            bool containsRussian = Regex.IsMatch(word, @"\p{IsCyrillic}");
            if (containsRussian) {
                foreach (var replacement in RuReplacements) {
                    pattern = pattern.Replace(replacement.Key.ToString(), replacement.Value);
                }
            } else {
                foreach (var replacement in EnReplacements) {
                    pattern = pattern.Replace(replacement.Key.ToString(), replacement.Value);
                }
            }
            patterns.Add(word, pattern);
            Logger.Verbose($"Adding {word}, with pattern: {pattern}");
        }

        return patterns;
    }

    public static void InitializeMapInfos() {
        Logger.Information("Parsing map infos");

        string mapInfosConfigPath = Path.Combine(ResourcesPath, "mapInfo.json");
        Dictionary<string, MapInfo> mapInfos = JsonConvert.DeserializeObject<Dictionary<string, MapInfo>>(File.ReadAllText(mapInfosConfigPath))!;

        foreach ((string mapName, MapInfo mapInfo) in mapInfos) {
            MapInfo info = mapInfo;
            info.Name = mapName;
            mapInfos[mapName] = info;
        }

        MapInfos = mapInfos.Values.ToFrozenSet();
        Logger.Information("Map infos parsed");
    }

    public static void InitializeMapModels() {
        Logger.Information("Parsing map models");

        string mapModelsConfigPath = Path.Combine(ResourcesPath, "MapModels");
        Vector3 gltfToUnity = new(-1, 1, 1);

        ConcurrentDictionary<string, Triangle[]> mapNameToTriangles = new();

        Parallel.ForEach(Directory.EnumerateFiles(mapModelsConfigPath),
            mapModelPath => {
                string mapName = Path.GetFileNameWithoutExtension(mapModelPath);
                Logger.Debug("Parsing {MapName}", mapName);

                try {
                    ModelRoot mapRoot = ModelRoot.Load(mapModelPath);

                    Triangle[] triangles = mapRoot.DefaultScene // todo create a mesh immediately instead of store list of triangles
                        .EvaluateTriangles()
                        .Select(tuple =>
                            new Triangle(
                                tuple.A.GetGeometry().GetPosition() * gltfToUnity,
                                tuple.B.GetGeometry().GetPosition() * gltfToUnity,
                                tuple.C.GetGeometry().GetPosition() * gltfToUnity))
                        .ToArray();

                    mapNameToTriangles[mapName] = triangles;
                } catch (Exception e) {
                    Logger.Error(e, "An exception occured while generating {MapName} map model", mapName);
                }
            });

        MapNameToTriangles = mapNameToTriangles.ToFrozenDictionary();
        Logger.Information("Map models parsed");
    }

    public static void InitializeCache() {
        Logger.Information("Generating config archives");

        string rootPath = Path.Combine(ResourcesPath, "Configuration");
        string configsPath = Path.Combine(rootPath, "configs");
        string localizationsPath = Path.Combine(rootPath, "localization");

        Dictionary<string, byte[]> localeToConfigCache = new(2);

        foreach (string localeDir in Directory.EnumerateDirectories(localizationsPath)) {
            string locale = new DirectoryInfo(localeDir).Name;

            Logger.Debug("Generating archive for the '{Locale}' locale", locale);

            using MemoryStream outStream = new();

            using (IWriter writer = WriterFactory.Open(outStream, ArchiveType.Tar, new GZipWriterOptions())) {
                writer.WriteAll(configsPath, "*", SearchOption.AllDirectories);
                writer.WriteAll(localeDir, "*", SearchOption.AllDirectories);
            }

            byte[] buffer = outStream.ToArray();
            localeToConfigCache[locale] = buffer;
        }

        LocaleToConfigCache = localeToConfigCache.ToFrozenDictionary();
        Logger.Information("Cache for config archives generated");
    }

    public static void InitializeNodes() {
        Logger.Information("Generating config nodes");

        string configsPath = Path.Combine(ResourcesPath, "Configuration", "configs");

        IDeserializer deserializer = new DeserializerBuilder()
            .WithNodeDeserializer(new ComponentDeserializer())
            .WithNodeTypeResolver(new ComponentDeserializer())
            .IgnoreUnmatchedProperties()
            .Build();

        Dictionary<string, object?> components = new();
        Dictionary<string, long?> ids = new();

        foreach (string filePath in Directory.EnumerateFiles(configsPath, "*.*", SearchOption.AllDirectories)) {
            string relativePath = Path.GetRelativePath(configsPath, filePath).Replace('\\', '/');
            string fileName = Path.GetFileName(filePath);

            if (string.IsNullOrEmpty(fileName)) continue;

            Logger.Verbose("Parsing {File}", relativePath);

            switch (fileName) {
                case "id.yml": {
                    Dictionary<string, long> obj =
                        new Deserializer().Deserialize<Dictionary<string, long>>(File.ReadAllText(filePath));

                    ids[relativePath[..^7]] = obj["id"];
                    break;
                }

                case "public.yml": {
                    components[relativePath[..^11]] = deserializer.Deserialize(File.ReadAllText(filePath));
                    break;
                }
            }

            foreach ((string key, object? value) in components) {
                if (value is not Dictionary<object, object> dict ||
                    dict.Values.All(v => v is not IComponent))
                    continue;

                ConfigNode curNode = Root;

                foreach (string part in key.Split('/')) {
                    if (curNode.Children.TryGetValue(part, out ConfigNode child))
                        curNode = child;
                    else {
                        ids.TryGetValue(key, out long? id);
                        curNode = curNode.Children[part] = new ConfigNode { Id = id };
                    }
                }

                foreach (object obj in dict.Values) {
                    if (obj is not IComponent component) continue;

                    Type componentType = component.GetType();

                    if (componentType.IsDefined(typeof(ProtocolIdAttribute)))
                        curNode.Components[componentType] = component;
                    else
                        curNode.ServerComponents[componentType] = component;
                }

                foreach (IComponent serverComponent in curNode.ServerComponents.Values) {
                    foreach (Type type in serverComponent
                                 .GetType()
                                 .FindInterfaces((type, iType) => type.IsGenericType &&
                                                                  ReferenceEquals(type.GetGenericTypeDefinition(), iType),
                                     typeof(IConvertible<>))) {
                        Type resultType = type.GenericTypeArguments[0];

                        curNode.Components.TryGetValue(resultType, out IComponent? component);
                        component ??= (IComponent)RuntimeHelpers.GetUninitializedObject(resultType);

                        type.GetMethod("Convert")!.Invoke(serverComponent, [component]);
                        curNode.Components.TryAdd(resultType, component);
                    }
                }
            }
        }

        Logger.Information("Config nodes generated");
    }

    public static void InitializeGlobalEntities() {
        Logger.Information("Generating global entities");

        List<Type> types = Assembly.GetExecutingAssembly().GetTypes().ToList();
        string rootPath = Path.Combine(ResourcesPath, "GlobalEntities");

        Dictionary<string, Dictionary<string, IEntity>> globalEntities = new();

        List<string> typesToLoad = JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(Path.Combine(rootPath, "typesToLoad.json")))!;

        foreach (string filePath in typesToLoad.Select(type => Path.Combine(rootPath, $"{type}.json"))) {
            string relativePath = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');
            string entitiesTypeName = Path.GetFileNameWithoutExtension(filePath);

            Logger.Verbose("Parsing {File}", relativePath);

            JArray jArray = JArray.Parse(File.ReadAllText(filePath));

            Dictionary<string, IEntity> entities = new(jArray.Count);

            foreach (JToken jToken in jArray) {
                string entityName = jToken["name"]!.ToObject<string>()!;

                Logger.Verbose("Generating '{Name}'", entityName);

                long entityId = jToken["id"]!.ToObject<long>();

                if (entityId == 0)
                    entityId = EntityRegistry.FreeId;

                JArray templateComponents = jToken["template"]!.ToObject<JArray>()!;
                string templateName = templateComponents[0].ToObject<string>()!;
                string configPath = templateComponents[1].ToObject<string>()!;

                JObject rawComponents = jToken["components"]!.ToObject<JObject>()!;

                List<IComponent> components = new(rawComponents.Count);

                foreach ((string rawComponentName, JToken? rawComponentProperties) in rawComponents) {
                    Type componentType = types.Single(type => type.Name == rawComponentName);
                    ConstructorInfo componentCtor = componentType.GetConstructors().First();
                    ParameterInfo[] ctorParameters = componentCtor.GetParameters();

                    List<object?> parameters = new(ctorParameters.Length);

                    parameters.AddRange(ctorParameters
                        .Select(ctorParameter => {
                            JToken? rawComponentProperty = rawComponentProperties![ctorParameter.Name!];

                            if (rawComponentProperty == null && ctorParameter.HasDefaultValue)
                                return ctorParameter.DefaultValue;

                            return rawComponentProperty?.ToObject(ctorParameter.ParameterType);
                        }));

                    components.Add((IComponent)componentCtor.Invoke(parameters.ToArray()));
                }

                Type templateType = types.Single(type => type.Name == templateName);
                ConstructorInfo templateCtor = templateType.GetConstructors().First();
                EntityTemplate template = (EntityTemplate)templateCtor.Invoke(null);

                IEntityBuilder entityBuilder = new EntityBuilder(entityId).WithTemplateAccessor(template, configPath);
                components.ForEach(component => entityBuilder.AddComponent(component));

                IEntity entity = entityBuilder.Build(false);

                entities[entityName] = entity;

                Logger.Verbose("Generated {Entity}", entity);
            }

            globalEntities[entitiesTypeName] = entities;
        }

        Logger.Debug("Generating nodes for global entities");

        foreach ((string entitiesTypeName, Dictionary<string, IEntity> entities) in globalEntities) {
            ConfigNode curNode = Root;

            if (curNode.Children.TryGetValue(entitiesTypeName, out ConfigNode child))
                curNode = child;
            else
                curNode = curNode.Children[entitiesTypeName] = new ConfigNode();

            foreach ((string entityName, IEntity entity) in entities)
                curNode.Entities[entityName] = entity;
        }

        Logger.Information("Global entities generated");
    }

    public static void InitializeConfigs() {
        Logger.Information("Initializing configs");

        Discord = JsonConvert.DeserializeObject<DiscordConfig>(File.ReadAllText(Path.Combine(ResourcesPath, "discord.json")));
        ModulePrices = JsonConvert.DeserializeObject<ModulePrices>(File.ReadAllText(Path.Combine(ResourcesPath, "modulePrices.json")))!;
        Blueprints = JsonConvert
            .DeserializeObject<Dictionary<string, BlueprintChest>>(File.ReadAllText(Path.Combine(ResourcesPath, "blueprints.json")))!
            .ToFrozenDictionary();

        Logger.Information("Configs initialized");
    }

    public static IEntity GetGlobalEntity(string path, string entityName) {
        ConfigNode node = GetNode(path)!.Value;

        return node.Entities[entityName].Clone();
    }

    public static IEnumerable<IEntity> GetGlobalEntities(string path) {
        ConfigNode node = GetNode(path)!.Value;

        return node.Entities.Values.Select(entity => entity.Clone());
    }

    public static IEnumerable<IEntity> GetGlobalEntities() {
        string[] excluded = ["moduleSlots"];

        return Root.Children
            .Where(child => !excluded.Contains(child.Key))
            .SelectMany(child =>
                child.Value.Entities.Values.Select(entity => entity.Clone()));
    }

    public static T GetComponent<T>(string path) where T : class, IComponent =>
        GetComponentOrNull<T>(path)!;

    public static T? GetComponentOrNull<T>(string path) where T : class, IComponent {
        ConfigNode? node = GetNode(path);

        if (!node.HasValue) return null;

        if (!node.Value.Components.TryGetValue(typeof(T), out IComponent? component))
            node.Value.ServerComponents.TryGetValue(typeof(T), out component);

        return component?.Clone() as T;
    }

    public static bool TryGetComponent<T>(string path, [NotNullWhen(true)] out T? component) where T : class, IComponent =>
        (component = GetComponentOrNull<T>(path)) != null;

    public static bool TryGetConfig(string locale, [NotNullWhen(true)] out byte[]? config) =>
        LocaleToConfigCache.TryGetValue(locale, out config);

    static ConfigNode? GetNode(string path) {
        ConfigNode curNode = Root;

        if (string.IsNullOrWhiteSpace(path)) return null;

        path = path.Replace('\\', '/');

        if (path.StartsWith('/')) path = path[1..];

        foreach (string part in path.Split('/'))
            if (curNode.Children.TryGetValue(part, out ConfigNode child))
                curNode = child;
            else return null;

        return curNode;
    }

    record struct ConfigNode() {
        public long? Id { get; init; }
        public Dictionary<Type, IComponent> Components { get; } = new();
        public Dictionary<Type, IComponent> ServerComponents { get; } = new();
        public Dictionary<string, IEntity> Entities { get; } = new();
        public Dictionary<string, ConfigNode> Children { get; } = new();
    }
}

// Copied from https://github.com/Assasans/TXServer-Public/blob/database/TXServer/Core/Configuration/ComponentDeserializer.cs
public partial class ComponentDeserializer : INodeTypeResolver, INodeDeserializer {
    ILogger Logger { get; } = Log.Logger.ForType(typeof(ComponentDeserializer));
    Type? Type { get; set; }

    IEnumerable<Type> Types { get; } = Assembly.GetExecutingAssembly().GetTypes();

    public bool Deserialize(
        IParser reader,
        Type expectedType,
        Func<IParser, Type, object?> nestedObjectDeserializer,
        out object? retValue) {
        if (!typeof(IComponent).IsAssignableFrom(expectedType)) {
            retValue = null;
            return false;
        }

        IComponent component = (IComponent)RuntimeHelpers.GetUninitializedObject(expectedType);

        reader.MoveNext();

        while (reader.Current != null && reader.Current is not MappingEnd) {
            if (reader.Current is not Scalar scalar) continue;

            string key = scalar.Value[0].ToString().ToUpper() + scalar.Value[1..];
            PropertyInfo? info = expectedType.GetProperty(key);

            reader.MoveNext();

            if (info == null) {
                reader.SkipThisAndNestedEvents();
                continue;
            }

            object? value = nestedObjectDeserializer(reader, info.PropertyType);
            info.SetValue(component, value);

            Logger.Verbose(">> {Key}: {Value}", key, value);
        }

        reader.MoveNext();

        retValue = component;
        return true;
    }

    public bool Resolve(NodeEvent? nodeEvent, ref Type currentType) {
        if (Type != null) {
            Type type = Type;
            Type = null;

            if (nodeEvent is not MappingStart)
                return false;

            Logger.Debug("> {Type}", type);
            currentType = type;
            return true;
        }

        if (nodeEvent is not Scalar scalar ||
            scalar.Value.Length < 2 ||
            !MyRegex().IsMatch(scalar.Value)) return false;

        string typeName =
            $"{scalar.Value[0].ToString().ToUpper()}{scalar.Value[1..]}Component";

        List<Type> types = Types.Where(type => type.Name == typeName).ToList();

        Type? resolvedType = types.FirstOrDefault(type => !Attribute.IsDefined(type, typeof(ProtocolIdAttribute))) ??
                             types.FirstOrDefault();

        if (resolvedType != null)
            Type = resolvedType;

        return false;
    }

    [GeneratedRegex("^[a-zA-Z]+$")]
    private static partial Regex MyRegex();
}