using UnityEngine;

/// <summary>
/// Creates code-driven particle effects for wine prefabs.
/// Call the static methods from your spawn logic — each returns the created GameObject
/// so you can parent/position it yourself if needed.
/// </summary>
public static class WineParticleFactory
{
    // ───────────────────────────────────────────────
    //  wine_01  —  yellow-green magical fountain sparkle
    // ───────────────────────────────────────────────
    public static GameObject CreateWine01Particles(Transform parent)
    {
        var go = new GameObject("Wine01Particles");
        go.transform.SetParent(parent, false);
        // Offset: upper-center of the bottle — particles spread downward from here
        go.transform.localPosition = new Vector3(0f, 0.20f, 0f);

        var ps = go.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.duration           = 10f;
        main.loop               = true;
        main.startLifetime      = new ParticleSystem.MinMaxCurve(0.8f, 1.6f);
        main.startSpeed         = new ParticleSystem.MinMaxCurve(0.08f, 0.22f);
        main.startSize          = new ParticleSystem.MinMaxCurve(0.004f, 0.012f);
        main.simulationSpace    = ParticleSystemSimulationSpace.Local;
        main.maxParticles       = 300;
        main.gravityModifier    = -0.02f; // gentle upward drift

        // Color over lifetime: golden-yellow → green → fade out
        var col = ps.colorOverLifetime;
        col.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(1f, 0.92f, 0.3f), 0f),     // warm gold
                new GradientColorKey(new Color(0.6f, 0.95f, 0.35f), 0.4f), // citrus green
                new GradientColorKey(new Color(0.3f, 0.85f, 0.3f), 0.75f), // green
                new GradientColorKey(new Color(0.3f, 0.85f, 0.3f), 1f),
            },
            new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(0.9f, 0.15f),
                new GradientAlphaKey(0.7f, 0.6f),
                new GradientAlphaKey(0f, 1f),
            }
        );
        col.color = new ParticleSystem.MinMaxGradient(gradient);

        // Emission — lively burst feel
        var emission = ps.emission;
        emission.rateOverTime = 60f;
        // Small periodic bursts for the "sparkle" quality
        emission.SetBursts(new[]
        {
            new ParticleSystem.Burst(0f, 8, 14, 6, 0.4f),
        });

        // Shape — small sphere around bottle center
        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius    = 0.03f;

        // Size over lifetime — shrink toward end
        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 0.2f));

        // Velocity over lifetime — gentle random spread + upward bias
        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.x = new ParticleSystem.MinMaxCurve(-0.02f, 0.02f);
        vel.y = new ParticleSystem.MinMaxCurve(0.03f, 0.08f);
        vel.z = new ParticleSystem.MinMaxCurve(-0.02f, 0.02f);

        // Renderer — use default particle material, additive
        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.material   = GetParticleMaterial();

        ps.Play();
        return go;
    }

    // ───────────────────────────────────────────────
    //  wine_02  —  white sparkling carbonation bubbles
    // ───────────────────────────────────────────────
    public static GameObject CreateWine02Particles(Transform parent)
    {
        var go = new GameObject("Wine02Particles");
        go.transform.SetParent(parent, false);
        // Offset: start near the bottom of the bottle, bubbles rise up
        go.transform.localPosition = new Vector3(0f, 0.04f, 0f);

        var ps = go.AddComponent<ParticleSystem>();
        var main = ps.main;
        main.duration           = 10f;
        main.loop               = true;
        main.startLifetime      = new ParticleSystem.MinMaxCurve(1.2f, 2.5f);
        main.startSpeed         = new ParticleSystem.MinMaxCurve(0.04f, 0.10f);
        main.startSize          = new ParticleSystem.MinMaxCurve(0.002f, 0.006f);
        main.simulationSpace    = ParticleSystemSimulationSpace.Local;
        main.maxParticles       = 200;
        main.gravityModifier    = -0.015f; // gentle upward float

        // Color over lifetime: white with gentle alpha fade
        var col = ps.colorOverLifetime;
        col.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(1f, 1f, 1f), 0f),
                new GradientColorKey(new Color(0.95f, 0.97f, 1f), 0.5f),
                new GradientColorKey(new Color(0.9f, 0.95f, 1f), 1f),
            },
            new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(0.6f, 0.1f),
                new GradientAlphaKey(0.5f, 0.5f),
                new GradientAlphaKey(0f, 1f),
            }
        );
        col.color = new ParticleSystem.MinMaxGradient(gradient);

        // Emission — steady, delicate stream
        var emission = ps.emission;
        emission.rateOverTime = 30f;

        // Shape — wider vertical column for broader bubble spread
        var shape = ps.shape;
        shape.shapeType      = ParticleSystemShapeType.Cone;
        shape.angle          = 12f;
        shape.radius         = 0.06f;
        shape.rotation       = new Vector3(-90f, 0f, 0f); // emit upward

        // Size over lifetime — tiny pop then shrink
        var size = ps.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 0.5f),
            new Keyframe(0.2f, 1f),
            new Keyframe(1f, 0.3f)
        ));

        // Velocity over lifetime — mostly vertical with minimal drift
        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        vel.x = new ParticleSystem.MinMaxCurve(-0.005f, 0.005f);
        vel.y = new ParticleSystem.MinMaxCurve(0.02f, 0.06f);
        vel.z = new ParticleSystem.MinMaxCurve(-0.005f, 0.005f);

        // Renderer
        var renderer = go.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.material   = GetParticleMaterial();

        ps.Play();
        return go;
    }

    // ───────────────────────────────────────────────
    //  Shared helpers
    // ───────────────────────────────────────────────

    private static Texture2D _cachedCircleTex;

    /// <summary>
    /// Generates a soft circular gradient texture at runtime so particles
    /// appear round instead of square. Cached after first call.
    /// </summary>
    private static Texture2D GetCircleTexture()
    {
        if (_cachedCircleTex != null)
            return _cachedCircleTex;

        const int res = 64;
        var tex = new Texture2D(res, res, TextureFormat.RGBA32, false);
        var center = new Vector2(res * 0.5f, res * 0.5f);
        var maxRadius = res * 0.5f;

        for (var y = 0; y < res; y++)
        {
            for (var x = 0; x < res; x++)
            {
                var dist = Vector2.Distance(new Vector2(x, y), center) / maxRadius;
                var alpha = Mathf.Clamp01(1f - dist * dist); // soft falloff
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        tex.Apply();
        _cachedCircleTex = tex;
        return tex;
    }

    /// <summary>
    /// Returns an additive particle material with a round soft-circle texture.
    /// Works at runtime without any asset dependency.
    /// </summary>
    private static Material GetParticleMaterial()
    {
        var shader = Shader.Find("Particles/Standard Unlit");
        if (shader == null)
            shader = Shader.Find("Legacy Shaders/Particles/Additive");
        if (shader == null)
            shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null)
        {
            Debug.LogWarning("[WineParticles] No particle shader found — using default sprite");
            shader = Shader.Find("Sprites/Default");
        }

        var mat = new Material(shader);
        mat.mainTexture = GetCircleTexture();
        mat.SetFloat("_Mode", 1f); // additive
        mat.renderQueue = 3000;
        return mat;
    }
}
