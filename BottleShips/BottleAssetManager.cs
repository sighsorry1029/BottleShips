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
    private const int IconLayer = 30;
    private const int EmbeddedIconSize = 64;
    private const int EmbeddedIconBytesPerPixel = 4;
    private const float IconSnapshotZoom = 2f;
    private static readonly Vector3 IconSnapshotRotation = new(135f, 90f, 135f);

    internal static void RegisterBottleItem(string assetBundleName, string prefabName)
    {
        GameObject prefab = LoadBundle(assetBundleName).LoadAsset<GameObject>(prefabName);
        if (prefab == null)
        {
            BottleShipsPlugin.BottleShipsLogger.LogWarning($"Bottle prefab '{prefabName}' was not found in asset bundle '{assetBundleName}'.");
            return;
        }

        if (prefab.TryGetComponent(out ItemDrop itemDrop))
        {
            itemDrop.m_itemData.m_dropPrefab = prefab;
            if (!ApplyEmbeddedIcon(itemDrop, prefabName))
            {
                SnapshotItem(itemDrop);
            }
        }
        else
        {
            BottleShipsPlugin.BottleShipsLogger.LogWarning($"Bottle prefab '{prefabName}' has no ItemDrop component.");
        }

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

            if (!objectDB.m_items.Contains(prefab))
            {
                objectDB.m_items.Add(prefab);
            }

            RegisterStatusEffect(objectDB, itemDrop.m_itemData.m_shared.m_attackStatusEffect);
            RegisterStatusEffect(objectDB, itemDrop.m_itemData.m_shared.m_consumeStatusEffect);
            RegisterStatusEffect(objectDB, itemDrop.m_itemData.m_shared.m_equipStatusEffect);
            RegisterStatusEffect(objectDB, itemDrop.m_itemData.m_shared.m_setStatusEffect);
        }

        objectDB.UpdateRegisters();
    }

    internal static void RegisterWithZNetScene(ZNetScene scene)
    {
        foreach (GameObject prefab in BottleItemPrefabs)
        {
            if (prefab == null)
            {
                continue;
            }

            if (!scene.m_prefabs.Contains(prefab))
            {
                scene.m_prefabs.Add(prefab);
            }

        }
    }

    internal static void RefreshBottleIcons()
    {
        bool refreshed = false;
        foreach (GameObject prefab in BottleItemPrefabs)
        {
            if (prefab != null && prefab.TryGetComponent(out ItemDrop itemDrop))
            {
                if (!ApplyEmbeddedIcon(itemDrop, prefab.name))
                {
                    SnapshotItem(itemDrop);
                }

                refreshed = true;
            }
        }

        if (refreshed)
        {
            RefreshVisibleItemUi();
        }
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

        bundle = AssetBundle.LoadFromStream(stream);
        Bundles[assetBundleName] = bundle;
        return bundle;
    }

    private static bool ApplyEmbeddedIcon(ItemDrop item, string prefabName)
    {
        Sprite? icon = LoadEmbeddedIcon(prefabName);
        if (icon == null)
        {
            return false;
        }

        item.m_itemData.m_shared.m_icons = new[] { icon };
        return true;
    }

    private static Sprite? LoadEmbeddedIcon(string prefabName)
    {
        if (IconSprites.TryGetValue(prefabName, out Sprite cachedIcon) && cachedIcon != null)
        {
            return cachedIcon;
        }

        string resourceName = $"{Assembly.GetExecutingAssembly().GetName().Name}.assets.icons.{prefabName}.rgba";
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            BottleShipsPlugin.BottleShipsLogger.LogWarning($"Embedded bottle icon '{resourceName}' was not found. Falling back to runtime icon snapshot.");
            return null;
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
            BottleShipsPlugin.BottleShipsLogger.LogWarning($"Embedded bottle icon '{resourceName}' has invalid size. Falling back to runtime icon snapshot.");
            return null;
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

    private static void SnapshotItem(ItemDrop item)
    {
        void Render()
        {
            Rect rect = new(0, 0, 64, 64);

            Camera camera = new GameObject("BottleShipsIconCamera", typeof(Camera)).GetComponent<Camera>();
            camera.backgroundColor = Color.clear;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.fieldOfView = 0.5f;
            camera.farClipPlane = 10000000;
            camera.cullingMask = 1 << IconLayer;
            camera.transform.rotation = Quaternion.Euler(90f, 0f, 45f);

            Light light = new GameObject("BottleShipsIconLight", typeof(Light)).GetComponent<Light>();
            light.transform.rotation = Quaternion.Euler(150, 0, -5f);
            light.type = LightType.Directional;
            light.cullingMask = 1 << IconLayer;
            light.intensity = 1.3f;

            GameObject visual;
            if (item.transform.Find("attach") is { } attach)
            {
                visual = UnityEngine.Object.Instantiate(attach.gameObject);
            }
            else
            {
                try
                {
                    ZNetView.m_forceDisableInit = true;
                    visual = UnityEngine.Object.Instantiate(item.gameObject);
                }
                finally
                {
                    ZNetView.m_forceDisableInit = false;
                }
            }

            visual.transform.rotation = Quaternion.Euler(IconSnapshotRotation);
            visual.SetActive(true);

            foreach (Transform child in visual.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                child.gameObject.layer = IconLayer;
                child.gameObject.SetActive(true);
            }

            Renderer[] allRenderers = visual.GetComponentsInChildren<Renderer>(includeInactive: true)
                .Where(IsRenderableIconRenderer)
                .ToArray();
            Renderer[] mainRenderers = allRenderers
                .Where(renderer => !LooksLikeEffectRenderer(renderer))
                .ToArray();
            Renderer[] renderers = mainRenderers.Length > 0 ? mainRenderers : allRenderers;
            if (renderers.Length == 0)
            {
                UnityEngine.Object.DestroyImmediate(visual);
                UnityEngine.Object.Destroy(camera.gameObject);
                UnityEngine.Object.Destroy(light.gameObject);
                return;
            }

            HashSet<Renderer> selectedRenderers = new(renderers);
            foreach (Renderer renderer in allRenderers)
            {
                renderer.enabled = selectedRenderers.Contains(renderer);
            }

            Vector3 min = renderers.Aggregate(Vector3.positiveInfinity,
                (current, renderer) => Vector3.Min(current, renderer.bounds.min));
            Vector3 max = renderers.Aggregate(Vector3.negativeInfinity,
                (current, renderer) => Vector3.Max(current, renderer.bounds.max));
            Vector3 size = max - min;

            RenderTexture renderTexture = RenderTexture.GetTemporary((int)rect.width, (int)rect.height);
            camera.targetTexture = renderTexture;

            float maxDim = Mathf.Max(size.x, size.z);
            float minDim = Mathf.Min(size.x, size.z);
            float yDist = (maxDim + minDim) / Mathf.Sqrt(2) / Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad);
            yDist /= IconSnapshotZoom;
            camera.transform.position = ((min + max) / 2) with { y = max.y } + new Vector3(0, yDist, 0);
            light.transform.position = camera.transform.position + new Vector3(-2, 0, 0.2f) / 3 * -yDist;

            camera.Render();

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = renderTexture;

            Texture2D texture = new((int)rect.width, (int)rect.height, TextureFormat.RGBA32, false);
            texture.ReadPixels(rect, 0, 0);
            texture.Apply();
            item.m_itemData.m_shared.m_icons = new[] { Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f)) };

            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(renderTexture);
            UnityEngine.Object.DestroyImmediate(visual);
            UnityEngine.Object.Destroy(camera.gameObject);
            UnityEngine.Object.Destroy(light.gameObject);
        }

        if (ObjectDB.instance != null)
        {
            Render();
        }
        else
        {
            BottleShipsPlugin.Instance.StartCoroutine(RenderNextFrame(Render));
        }
    }

    private static bool IsRenderableIconRenderer(Renderer renderer)
    {
        return renderer != null &&
               renderer is not ParticleSystemRenderer &&
               renderer.sharedMaterials.Any(material => material != null);
    }

    private static bool LooksLikeEffectRenderer(Renderer renderer)
    {
        string path = GetTransformPath(renderer.transform).ToLowerInvariant();
        return path.Contains("flare") ||
               path.Contains("glow") ||
               path.Contains("vfx") ||
               path.Contains("fx_") ||
               path.Contains("_fx") ||
               path.Contains("particle") ||
               path.Contains("spark") ||
               path.Contains("light") ||
               path.Contains("smoke") ||
               path.Contains("mist") ||
               path.Contains("aura") ||
               path.Contains("beam");
    }

    private static string GetTransformPath(Transform transform)
    {
        List<string> parts = new();
        Transform? current = transform;
        while (current != null)
        {
            parts.Add(current.name);
            current = current.parent;
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    private static System.Collections.IEnumerator RenderNextFrame(Action render)
    {
        yield return null;
        render();
    }

    private static void RefreshVisibleItemUi()
    {
        if (InventoryGui.instance != null && Player.m_localPlayer != null)
        {
            InventoryGui.instance.UpdateInventory(Player.m_localPlayer);
            InventoryGui.instance.UpdateCraftingPanel();
            InventoryGui.instance.UpdateRecipe(Player.m_localPlayer, 0f);
        }
    }
}

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.Awake))]
internal static class BottleAssetObjectDBAwakePatch
{
    [HarmonyPriority(Priority.VeryHigh)]
    private static void Prefix(ObjectDB __instance)
    {
        BottleAssetManager.RegisterWithObjectDB(__instance);
    }
}

[HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
internal static class BottleAssetObjectDBCopyOtherDBPatch
{
    [HarmonyPriority(Priority.VeryHigh)]
    private static void Postfix(ObjectDB __instance)
    {
        BottleAssetManager.RegisterWithObjectDB(__instance);
    }
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
internal static class BottleAssetZNetSceneAwakePatch
{
    [HarmonyPriority(Priority.VeryHigh)]
    private static void Prefix(ZNetScene __instance)
    {
        BottleAssetManager.RegisterWithZNetScene(__instance);
    }
}
