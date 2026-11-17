namespace Roads.Rendering;

public static class Shaders
{
    public const string LitVertex = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec3 aColor;
        layout(location = 3) in vec3 aBary;
        layout(location = 4) in vec3 aObject;   // centro x, centro z, raggio dell'oggetto
        layout(location = 5) in vec2 aObjectY;  // quota minima e massima dell'oggetto

        uniform mat4 uModel;
        uniform mat4 uView;
        uniform mat4 uProj;
        uniform mat4 uLightSpace;
        uniform int uWater;
        uniform float uTime;
        uniform int uSeeThrough;  // 0 = normale, 1 = scenario opaco, 2 = scenario trasparente
        uniform vec3 uCameraPos;
        uniform vec3 uFocus;      // punto da tenere sempre visibile (il giocatore)

        out vec3 vColor;
        out vec3 vWorld;
        out vec4 vLightPos;
        out vec3 vBary;
        flat out float vFade;

        // Quanto l'oggetto (cilindro verticale) copre la linea di vista camera -> giocatore.
        // Dipende solo da dati comuni a tutti i vertici dell'oggetto, quindi vale per l'oggetto intero.
        float objectFade()
        {
            if (uSeeThrough == 0 || aObject.z <= 0.0) return 0.0;
            vec3 base = (uModel * vec4(aObject.x, aObjectY.x, aObject.y, 1.0)).xyz;
            float top = base.y + (aObjectY.y - aObjectY.x);
            vec2 a = uCameraPos.xz, ab = uFocus.xz - a;
            float t = clamp(dot(base.xz - a, ab) / max(dot(ab, ab), 1e-4), 0.0, 1.0);
            float dist = length(a + ab * t - base.xz);         // distanza orizzontale raggio-asse
            float rayY = mix(uCameraPos.y, uFocus.y, t);       // quota del raggio in quel punto
            float r = aObject.z;
            return (1.0 - smoothstep(r * 0.7 + 0.3, r + 1.4, dist))
                 * (1.0 - smoothstep(top - 0.3, top + 0.8, rayY));
        }

        void main()
        {
            vec4 w = uModel * vec4(aPos, 1.0);
            // Onde low poly sulla superficie dell'acqua (non sulle cascate)
            if (uWater == 1 && aNormal.y > 0.5)
            {
                w.y += sin(w.x * 1.1 + uTime * 1.6) * 0.05
                     + cos(w.z * 1.4 + uTime * 1.2) * 0.05
                     + sin((w.x + w.z) * 2.3 + uTime * 2.5) * 0.025;
            }
            vWorld = w.xyz;
            vColor = aColor;
            vBary = aBary;
            vFade = objectFade();
            vLightPos = uLightSpace * w;
            gl_Position = uProj * uView * w;
        }
        """;

    /// <summary>Colore del cielo per una direzione di vista (condiviso da sfondo e nebbia).</summary>
    private const string SkyGlsl = """
        vec3 skyColor(vec3 d)
        {
            vec3 zenith  = vec3(0.30, 0.58, 0.92);
            vec3 horizon = vec3(0.88, 0.94, 0.98);
            vec3 below   = vec3(0.52, 0.76, 0.96);
            return d.y > 0.0 ? mix(horizon, zenith, pow(d.y, 0.55))
                             : mix(horizon, below, pow(-d.y, 0.45));
        }
        """;

    public static readonly string LitFragment = """
        #version 330 core
        in vec3 vColor;
        in vec3 vWorld;
        in vec4 vLightPos;
        in vec3 vBary;
        flat in float vFade;

        uniform sampler2D uShadowMap;
        uniform vec3 uLightDir;
        uniform vec3 uCameraPos;
        uniform float uFogNear;
        uniform float uFogFar;
        uniform float uEmissive;
        uniform float uTime;
        uniform int uWater;
        uniform int uCloud;
        uniform int uSeeThrough;

        out vec4 FragColor;

        //SKY

        float shadowFactor(float ndl)
        {
            vec3 p = vLightPos.xyz / vLightPos.w * 0.5 + 0.5;
            if (p.z > 1.0 || p.x < 0.0 || p.x > 1.0 || p.y < 0.0 || p.y > 1.0) return 1.0;
            float bias = max(0.002 * (1.0 - ndl), 0.0004);
            vec2 texel = 1.0 / vec2(textureSize(uShadowMap, 0));
            float lit = 0.0;
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                {
                    float d = texture(uShadowMap, p.xy + vec2(x, y) * texel).r;
                    lit += (p.z - bias > d) ? 0.0 : 1.0;
                }
            return lit / 9.0;
        }

        void main()
        {
            // Normale di faccia dalle derivate: shading piatto "low poly" garantito
            vec3 n = normalize(cross(dFdx(vWorld), dFdy(vWorld)));
            // Spigoli: distanza (in pixel) dal lato più vicino, calcolata prima di qualunque discard
            vec3 edgeAA = smoothstep(vec3(0.0), fwidth(vBary) * 1.4, vBary);
            float edge = 1.0 - min(min(edgeAA.x, edgeAA.y), edgeAA.z);

            // Pass opaco: gli oggetti che coprono il giocatore li disegna il pass trasparente
            const float FadeOn = 0.004;
            if (uSeeThrough == 1 && vFade > FadeOn) discard;
            if (uSeeThrough == 2 && vFade <= FadeOn) discard;
            vec3 L = -uLightDir;
            float ndl = max(dot(n, L), 0.0);
            float sh = ndl > 0.0 ? shadowFactor(ndl) : 0.0;

            vec3 hemi = mix(vec3(0.30, 0.32, 0.38), vec3(0.62, 0.70, 0.76), n.y * 0.5 + 0.5);
            vec3 col = vColor * (hemi * 0.8 + vec3(1.0, 0.93, 0.80) * ndl * sh * 0.68);
            col = mix(col, vColor * 1.25, uEmissive);

            float alpha = 1.0;
            if (uWater == 1)
            {
                vec3 V = normalize(uCameraPos - vWorld);
                vec3 H = normalize(L + V);
                col += vec3(1.0) * pow(max(dot(n, H), 0.0), 60.0) * 0.6 * sh;
                alpha = 0.8;
                if (abs(n.y) < 0.6)
                {
                    // Cascata: strisce di schiuma che scorrono verso il basso
                    float s = sin(vWorld.y * 6.0 + uTime * 9.0 + sin(vWorld.x * 3.0 + vWorld.z * 3.0) * 2.0);
                    col += vec3(0.25) * smoothstep(0.55, 0.9, s);
                    alpha = 0.85;
                }
            }

            if (uSeeThrough == 2)
            {
                // Facce quasi invisibili, spigoli ben marcati (leggermente più scuri del colore)
                col = mix(col, col * 0.55, edge);
                alpha = mix(1.0, mix(0.12, 0.95, edge), vFade);
            }

            vec3 toFrag = vWorld - uCameraPos;
            float d = length(toFrag);
            float fog = smoothstep(uFogNear, uFogFar, d);
            if (uCloud == 1)
                fog *= 0.7; // le nuvole restano visibili anche lontano
            else
                fog = max(fog, 1.0 - smoothstep(-12.0, -2.5, vWorld.y)); // ciò che sta sotto le isole sfuma
            FragColor = vec4(mix(col, skyColor(toFrag / d), fog), alpha);
        }
        """.Replace("//SKY", SkyGlsl);

    /// <summary>Sfondo: triangolo a tutto schermo, il colore dipende dalla direzione del raggio.</summary>
    public const string SkyVertex = """
        #version 330 core
        out vec2 vNdc;
        void main()
        {
            vec2 p = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            vNdc = p * 2.0 - 1.0;
            gl_Position = vec4(vNdc, 0.0, 1.0);
        }
        """;

    public static readonly string SkyFragment = """
        #version 330 core
        in vec2 vNdc;
        uniform mat4 uInvViewProj;
        out vec4 FragColor;

        //SKY

        void main()
        {
            vec4 a = uInvViewProj * vec4(vNdc, 0.0, 1.0);
            vec4 b = uInvViewProj * vec4(vNdc, 1.0, 1.0);
            vec3 dir = normalize(b.xyz / b.w - a.xyz / a.w);
            FragColor = vec4(skyColor(dir), 1.0);
        }
        """.Replace("//SKY", SkyGlsl);

    public const string DepthVertex = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        uniform mat4 uModel;
        uniform mat4 uLightSpace;
        void main() { gl_Position = uLightSpace * uModel * vec4(aPos, 1.0); }
        """;

    public const string DepthFragment = """
        #version 330 core
        void main() { }
        """;
}
