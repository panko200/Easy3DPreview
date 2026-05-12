using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Reflection;
using YukkuriMovieMaker.Project.Items;

#nullable enable
namespace Easy3DPreview;

internal static class Preview3DVisibilityJsonPatch
{
    private const string FieldName = "_isHiddenIn3DPreview";

    public static void Apply(Harmony harmony)
    {
        var targetMethod = AccessTools.Method(
            typeof(DefaultContractResolver),
            "CreateProperties",
            new[] { typeof(Type), typeof(MemberSerialization) });

        if (targetMethod == null) return;

        harmony.Patch(targetMethod, postfix: new HarmonyMethod(
            typeof(Preview3DVisibilityJsonPatch), nameof(Postfix)));
    }

    private static void Postfix(Type type, ref IList<JsonProperty> __result)
    {
        // VisualItem を継承する型のみ対象
        if (!typeof(VisualItem).IsAssignableFrom(type)) return;

        // 既に注入済みならスキップ
        foreach (var p in __result)
            if (p.PropertyName == FieldName) return;

        var injected = new JsonProperty
        {
            PropertyName = FieldName,
            PropertyType = typeof(bool),
            DeclaringType = type,
            ValueProvider = new Preview3DVisibilityValueProvider(),
            Readable = true,
            Writable = true,
            Required = Required.Default,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Populate,
            DefaultValue = false,
        };

        __result.Add(injected);
    }
}
