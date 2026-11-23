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
        uniform int uOnFoot;      // 1 = il giocatore è a piedi (le case sbiadiscono solo se la camera ci finisce dentro)
        uniform int uCamInside;   // 1 = la camera è dentro un edificio (il muro era troppo vicino al giocatore)

        out vec3 vColor;
        out vec3 vWorld;
        out vec4 vLightPos;
        out vec3 vBary;
        out vec3 vNormal;
        flat out float vFade;

        // Quanto l'oggetto (cilindro verticale) copre la linea di vista camera -> giocatore.
        // Dipende solo da dati comuni a tutti i vertici dell'oggetto, quindi vale per l'oggetto intero.
        float objectFade()
        {
            if (uSeeThrough == 0 || aObject.z == 0.0) return 0.0;
            vec3 base = (uModel * vec4(aObject.x, aObjectY.x, aObject.y, 1.0)).xyz;
            float top = base.y + (aObjectY.y - aObjectY.x);
            vec2 a = uCameraPos.xz, ab = uFocus.xz - a;
            float tRaw = dot(base.xz - a, ab) / max(dot(ab, ab), 1e-4);
            float t = clamp(tRaw, 0.0, 1.0);
            float dist = length(a + ab * t - base.xz);         // distanza orizzontale raggio-asse
            float rayY = mix(uCameraPos.y, uFocus.y, t);       // quota del raggio in quel punto
            float r = abs(aObject.z);
            // Solo ciò che sta tra la camera e il giocatore lo copre: quello che gli sta davanti (oltre di lui, guardando
            // dalla camera) il giocatore lo vede, e resta opaco anche se ci è addosso
            float beyond = (tRaw - 1.0) * length(ab);          // quanto il centro dell'oggetto è oltre il giocatore
            float onLine = (1.0 - smoothstep(r * 0.7 + 0.3, r + 1.4, dist))
                         * (1.0 - smoothstep(top - 0.3, top + 0.8, rayY))
                         * (1.0 - smoothstep(-0.2, 0.4, beyond));
            // Anche ciò che sta addosso alla camera (girando la visuale a piedi ci si finirebbe dentro)
            float nearCam = (1.0 - smoothstep(r + 0.4, r + 1.8, length(base.xz - a)))
                          * (1.0 - smoothstep(top, top + 0.6, uCameraPos.y));
            // Raggio negativo = edificio. A piedi le case restano solide: la camera non ci entra (si avvicina al giocatore).
            // Solo se il muro è troppo vicino e la camera ci finisce dentro, sbiadisce l'edificio che la contiene
            if (aObject.z < 0.0 && uOnFoot == 1)
                return uCamInside == 1 ? 1.0 - smoothstep(r * 1.2, r * 1.5, length(base.xz - a)) : 0.0;
            return max(onLine, nearCam);
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
            vNormal = mat3(uModel) * aNormal;
            vFade = objectFade();
            vLightPos = uLightSpace * w;
            gl_Position = uProj * uView * w;
        }
        """;

    /// <summary>
    /// Colore del cielo per una direzione di vista (condiviso da sfondo e nebbia).
    /// I tre colori cambiano con l'ora del giorno (DayCycle).
    /// </summary>
    private const string SkyGlsl = """
        uniform vec3 uSkyZenith;
        uniform vec3 uSkyHorizon;
        uniform vec3 uSkyBelow;

        vec3 skyColor(vec3 d)
        {
            return d.y > 0.0 ? mix(uSkyHorizon, uSkyZenith, pow(d.y, 0.55))
                             : mix(uSkyHorizon, uSkyBelow, pow(-d.y, 0.45));
        }
        """;

    public static readonly string LitFragment = """
        #version 330 core
        in vec3 vColor;
        in vec3 vWorld;
        in vec4 vLightPos;
        in vec3 vBary;
        in vec3 vNormal;
        flat in float vFade;

        uniform sampler2D uShadowMap;
        uniform int uSmooth;         // ombreggiatura morbida (normali interpolate) invece che a facce piatte
        uniform vec3 uLightDir;
        uniform vec3 uCameraPos;
        uniform float uFogNear;
        uniform float uFogFar;
        uniform float uFogMax;       // nebbia massima: 1 di solito, meno per i punti di riferimento (si vedono da lontano)
        uniform float uEmissive;
        uniform float uAlpha;        // opacità dell'oggetto (il personaggio sbiadisce guardando in su da sotto)
        uniform float uTime;
        uniform int uWater;
        uniform int uCloud;
        uniform int uSeeThrough;
        uniform vec3 uLightColor;    // sole di giorno, luna di notte (già moltiplicato per l'intensità)
        uniform vec3 uAmbientLow;    // luce ambiente dal basso e dall'alto
        uniform vec3 uAmbientHigh;
        uniform int uHeadOn;         // fari dell'auto: due fasci, uno per faro
        uniform vec3 uHeadPos[2];
        uniform vec3 uHeadDir[2];
        uniform float uHeadGlow;     // intensità dei fari: piena al buio, quasi nulla in pieno giorno
        uniform vec3 uTailPos;       // stop: bagliore rosso a terra dietro l'auto (di notte, in frenata)
        uniform vec3 uTailDir;
        uniform float uTailGlow;
        uniform int uLampCount;      // lampioni vicini: di notte fanno una pozza di luce calda sulla strada
        uniform vec3 uLampPos[24];
        uniform float uLampGlow;

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
            if (uSmooth == 1) n = normalize(vNormal);
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

            vec3 hemi = mix(uAmbientLow, uAmbientHigh, n.y * 0.5 + 0.5);
            vec3 col = vColor * (hemi * 0.8 + uLightColor * ndl * sh);

            // Fari: due coni di luce calda, uno per faro, che si attenuano con la distanza.
            // Vicino all'auto sono due chiazze distinte; più avanti si sovrappongono.
            if (uHeadOn == 1 && uCloud == 0 && uHeadGlow > 0.0)
            {
                for (int i = 0; i < 2; i++)
                {
                    vec3 toFrag = vWorld - uHeadPos[i];
                    float dist = length(toFrag);
                    vec3 l = toFrag / max(dist, 1e-3);
                    float cone = smoothstep(0.92, 0.985, dot(l, uHeadDir[i]));
                    float reach = (1.0 - smoothstep(16.0, 26.0, dist)) / (1.0 + dist * dist * 0.01);
                    float facing = 0.35 + 0.65 * max(dot(n, -l), 0.0);
                    col += vColor * vec3(1.0, 0.92, 0.72) * cone * reach * facing * 1.25 * uHeadGlow;
                }
            }
            // Bagliore degli stop: luce rossa che si allarga dietro l'auto, a corto raggio
            if (uTailGlow > 0.0 && uCloud == 0)
            {
                vec3 toFrag = vWorld - uTailPos;
                float dist = length(toFrag);
                vec3 l = toFrag / max(dist, 1e-3);
                float behind = smoothstep(-0.2, 0.6, dot(l, uTailDir));
                float reach = 1.0 - smoothstep(0.5, 5.5, dist);
                float facing = 0.4 + 0.6 * max(dot(n, -l), 0.0);
                col += vColor * vec3(1.0, 0.12, 0.08) * behind * reach * reach * facing * uTailGlow;
            }
            // Lampioni: luce che scende dalla lampada e si allarga a terra, più forte proprio sotto
            if (uLampGlow > 0.0 && uCloud == 0)
            {
                vec3 lamp = vec3(0.0);
                for (int i = 0; i < uLampCount; i++)
                {
                    vec3 toFrag = vWorld - uLampPos[i];
                    float dist = length(toFrag);
                    vec3 l = toFrag / max(dist, 1e-3);
                    float down = 0.35 + 0.65 * smoothstep(-0.2, 0.7, -l.y); // più luce verso il basso che di lato
                    float reach = 1.0 - smoothstep(1.5, 8.5, dist);
                    float facing = 0.3 + 0.7 * max(dot(n, -l), 0.0);
                    lamp += reach * reach * down * facing;
                }
                col += vColor * vec3(1.0, 0.8, 0.5) * min(lamp, 1.6) * 1.3 * uLampGlow;
            }
            col = mix(col, vColor * 1.25, min(uEmissive, 1.0));
            // Oltre 1 la luce si fa più intensa restando del suo colore (stop in frenata: rosso pieno e acceso)
            float boost = max(uEmissive - 1.0, 0.0);
            col += vColor * vec3(0.7, 0.25, 0.25) * boost;

            float alpha = 1.0;
            if (uWater == 1)
            {
                vec3 V = normalize(uCameraPos - vWorld);
                vec3 H = normalize(L + V);
                col += uLightColor * pow(max(dot(n, H), 0.0), 60.0) * 0.9 * sh;
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
            fog = min(fog, uFogMax);
            FragColor = vec4(mix(col, skyColor(toFrag / d), fog), alpha * uAlpha);
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
        uniform float uStars;   // 0 di giorno, 1 a notte fonda
        uniform float uTime;
        out vec4 FragColor;

        //SKY

        void main()
        {
            vec4 a = uInvViewProj * vec4(vNdc, 0.0, 1.0);
            vec4 b = uInvViewProj * vec4(vNdc, 1.0, 1.0);
            vec3 dir = normalize(b.xyz / b.w - a.xyz / a.w);
            vec3 col = skyColor(dir);

            // Stelle: celle della sfera celeste accese a caso, con un leggero scintillio
            if (uStars > 0.0 && dir.y > 0.0)
            {
                vec3 cell = floor(dir * 240.0);
                float h = fract(sin(dot(cell, vec3(12.9898, 78.233, 37.719))) * 43758.5453);
                float twinkle = 0.65 + 0.35 * sin(uTime * 2.5 + h * 120.0);
                col += vec3(0.92, 0.94, 1.0) * step(0.9972, h) * twinkle * uStars * smoothstep(0.02, 0.25, dir.y);
            }
            FragColor = vec4(col, 1.0);
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
