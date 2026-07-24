using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BottleShips;

internal static class BottleAssetManager
{
    private static readonly Dictionary<string, AssetBundle> Bundles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Sprite> IconSprites = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<GameObject> BottleItemPrefabs = new();
    private const int EmbeddedIconSize = 64;
    private const int EmbeddedIconBytesPerPixel = 4;

    internal static void RegisterBottleItem(string assetBundleName, string prefabName)
    {
        GameObject prefab = LoadBundle(assetBundleName).LoadAsset<GameObject>(prefabName);
        if (prefab == null)
        {
            throw new InvalidOperationException(
                $"Bottle prefab '{prefabName}' was not found in asset bundle '{assetBundleName}'.");
        }

        if (!prefab.TryGetComponent(out ItemDrop itemDrop))
        {
            throw new InvalidOperationException($"Bottle prefab '{prefabName}' has no ItemDrop component.");
        }

        itemDrop.m_itemData.m_dropPrefab = prefab;
        ApplyEmbeddedIcon(itemDrop, prefabName);

        if (!BottleItemPrefabs.Any(existing => existing != null && existing.name == prefab.name))
        {
            BottleItemPrefabs.Add(prefab);
        }
    }

    internal static void RegisterWithObjectDB(ObjectDB objectDB)
    {
        foreach (GameObject prefab in BottleItemPrefabs)
        {
            if (prefab == null || !prefab.TryGetComponent(out ItemDrop itemDrop))
            {
                continue;
            }

            RegisterPrefabOrThrow(objectDB.m_items, prefab, nameof(ObjectDB));

            RegisterStatusEffect(objectDB, itemDrop.m_itemData.m_shared.m_attackStatusEffect);
            RegisterStatusEffect(objectDB, itemDrop.m_itemData.m_shared.m_consumeStatusEffect);
            RegisterStatusEffect(objectDB, itemDrop.m_itemData.m_shared.m_equipStatusEffect);
            RegisterStatusEffect(objectDB, itemDrop.m_itemData.m_shared.m_setStatusEffect);
        }
    }

    internal static void RegisterWithZNetScene(ZNetScene scene)
    {
        foreach (GameObject prefab in BottleItemPrefabs)
        {
            if (prefab == null)
            {
                continue;
            }

            RegisterPrefabOrThrow(scene.m_prefabs, prefab, nameof(ZNetScene));
        }
    }

    private static void RegisterPrefabOrThrow(
        List<GameObject> registeredPrefabs,
        GameObject prefab,
        string registryName)
    {
        foreach (GameObject existing in registeredPrefabs)
        {
            if (existing == prefab)
            {
                return;
            }

            if (existing != null && string.Equals(existing.name, prefab.name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Cannot register BottleShips prefab '{prefab.name}' with {registryName}: " +
                    "a different prefab with the same name is already registered.");
            }
        }

        registeredPrefabs.Add(prefab);
    }

    private static AssetBundle LoadBundle(string assetBundleName)
    {
        if (Bundles.TryGetValue(assetBundleName, out AssetBundle bundle) && bundle != null)
        {
            return bundle;
        }

        string resourceName = $"{Assembly.GetExecutingAssembly().GetName().Name}.assets.{assetBundleName}";
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new InvalidOperationException($"Embedded asset bundle '{resourceName}' was not found.");
        }

        AssetBundle? loadedBundle = AssetBundle.LoadFromStream(stream);
        if (loadedBundle == null)
        {
            throw new InvalidOperationException($"Embedded asset bundle '{resourceName}' could not be loaded.");
        }

        Bundles[assetBundleName] = loadedBundle;
        return loadedBundle;
    }

    private static void ApplyEmbeddedIcon(ItemDrop item, string prefabName)
    {
        item.m_itemData.m_shared.m_icons = new[] { LoadEmbeddedIcon(prefabName) };
    }

    private static Sprite LoadEmbeddedIcon(string prefabName)
    {
        if (IconSprites.TryGetValue(prefabName, out Sprite cachedIcon) && cachedIcon != null)
        {
            return cachedIcon;
        }

        string resourceName = $"{Assembly.GetExecutingAssembly().GetName().Name}.assets.icons.{prefabName}.rgba";
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new InvalidOperationException($"Embedded bottle icon '{resourceName}' was not found.");
        }

        int expectedLength = EmbeddedIconSize * EmbeddedIconSize * EmbeddedIconBytesPerPixel;
        byte[] rawTextureData = new byte[expectedLength];
        int bytesRead = 0;
        while (bytesRead < rawTextureData.Length)
        {
            int read = stream.Read(rawTextureData, bytesRead, rawTextureData.Length - bytesRead);
            if (read == 0)
            {
                break;
            }

            bytesRead += read;
        }

        if (bytesRead != expectedLength || stream.ReadByte() != -1)
        {
            throw new InvalidOperationException(
                $"Embedded bottle icon '{resourceName}' has an invalid size. Expected exactly {expectedLength} bytes.");
        }

        Texture2D texture = new(EmbeddedIconSize, EmbeddedIconSize, TextureFormat.RGBA32, false)
        {
            name = $"{prefabName}_icon",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };

        texture.LoadRawTextureData(rawTextureData);
        texture.Apply(false, false);

        Rect rect = new(0, 0, texture.width, texture.height);
        Sprite icon = Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f));
        icon.name = $"{prefabName}_icon";

        IconSprites[prefabName] = icon;
        return icon;
    }

    private static void RegisterStatusEffect(ObjectDB objectDB, StatusEffect? statusEffect)
    {
        if (statusEffect != null && !objectDB.GetStatusEffect(statusEffect.name.GetStableHashCode()))
        {
            objectDB.m_StatusEffects.Add(statusEffect);
        }
    }
}

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
internal static class BottleAssetObjectDBAwakePatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(ObjectDB __instance)
    {
        BottleAssetManager.RegisterWithObjectDB(__instance);
    }
}

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
internal static class BottleAssetObjectDBCopyOtherDBPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(ObjectDB __instance)
    {
        BottleAssetManager.RegisterWithObjectDB(__instance);
        __instance.UpdateRegisters();
        BottleShipsManager.QueueApply();
    }
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
internal static class BottleAssetZNetSceneAwakePatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Prefix(ZNetScene __instance)
    {
        BottleAssetManager.RegisterWithZNetScene(__instance);
    }
}
