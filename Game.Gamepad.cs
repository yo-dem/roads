using System.Numerics;
using Silk.NET.Input;

namespace Roads;

/// <summary>
/// Controller (PS4 o qualunque gamepad riconosciuto), insieme a tastiera e mouse: i comandi si sommano.
/// <code>
///   Comandi come in GTA 5:
///   levetta sinistra   sterzo (in auto) / cammina (a piedi; appena inclinata va piano)
///   R2 / L2            gas / freno e retromarcia (dosabili)
///   R1                 freno a mano (con L2: inversione a U)
///   △                  scendi / sali in auto (e sul treno)
///   ✕                  a piedi: corri (tenuto premuto)
///   □                  a piedi: salta
///   L3                 clacson
///   croce destra       fari
///   croce sinistra     cambia personaggio
///   levetta destra     gira la visuale (a piedi, in treno, nella visuale lontana)
///   croce su / giù     visuale più vicina / più lontana
///   SHARE              visuale da dietro / lontana
///   OPTIONS            mappa dell'isola
/// </code>
/// Il controller si può collegare anche a gioco avviato.
/// </summary>
public sealed partial class Game
{
    private const float StickDeadzone = 0.18f;
    private const float PadLookSpeed = 700f; // "pixel di mouse" al secondo con la levetta destra tutta inclinata

    private readonly HashSet<IGamepad> _pads = new();
    private bool _rawTriggers; // alcuni driver danno i grilletti da -1 (a riposo) a 1 invece che da 0 a 1

    /// <summary>Quanto sono premuti i comandi del controller in questo istante.</summary>
    private readonly record struct PadInput(
        float SteerX, float MoveX, float MoveY, float Gas, float Brake, Vector2 Look,
        bool Handbrake, bool Slow, bool Sprint, bool Horn, bool ZoomIn, bool ZoomOut);

    private void SetupGamepads()
    {
        foreach (var g in _input.Gamepads) HookGamepad(g);
        _input.ConnectionChanged += (device, connected) =>
        {
            if (device is IGamepad g && connected) HookGamepad(g);
        };
    }

    private void HookGamepad(IGamepad g)
    {
        if (!g.IsConnected || !_pads.Add(g)) return;
        Console.WriteLine($"Controller collegato: {g.Name}");
        g.ButtonDown += (_, b) => OnPadButton(b.Name);
    }

    private void OnPadButton(ButtonName b)
    {
        switch (b)
        {
            case ButtonName.Y: ToggleOnFoot(); break;                                   // △
            case ButtonName.X when _onFoot: _jumpQueued = true; break;                  // □ a piedi: salto
            case ButtonName.DPadRight when !_onFoot: _player.Headlights = !_player.Headlights; break; // croce destra
            case ButtonName.Start: _bigMap = !_bigMap; break;                           // OPTIONS
            case ButtonName.Back: _farView = !_farView; break;                          // SHARE
            case ButtonName.DPadLeft:                                                   // croce sinistra
                _kid.CycleCharacter(1);
                Console.WriteLine($"Personaggio: {_kid.Character}");
                break;
        }
    }

    /// <summary>Lo stato del primo controller collegato (tutto a zero se non ce n'è).</summary>
    private PadInput ReadPad()
    {
        var g = _pads.FirstOrDefault(p => p.IsConnected);
        if (g == null) return default;

        Vector2 Stick(int i)
        {
            if (g.Thumbsticks.Count <= i) return Vector2.Zero;
            var v = new Vector2(g.Thumbsticks[i].X, g.Thumbsticks[i].Y);
            float len = v.Length();
            if (len < StickDeadzone) return Vector2.Zero;
            // Zona morta tonda, poi da 0 a 1 senza scalino
            return v / len * MathF.Min(1f, (len - StickDeadzone) / (1f - StickDeadzone));
        }
        float Trigger(int i)
        {
            if (g.Triggers.Count <= i) return 0f;
            float t = g.Triggers[i].Position;
            if (t < -0.05f) _rawTriggers = true;
            if (_rawTriggers) t = (t + 1f) * 0.5f;
            t = Math.Clamp(t, 0f, 1f);
            return t < 0.06f ? 0f : t;
        }
        bool Held(ButtonName name) => g.Buttons.Any(b => b.Name == name && b.Pressed);

        Vector2 left = Stick(0), right = Stick(1);
        float tilt = left.Length();
        // In su la levetta dà Y negativa
        return new PadInput(
            SteerX: left.X,
            MoveX: left.X, MoveY: -left.Y,
            Gas: Trigger(1), Brake: Trigger(0), Look: right,
            Handbrake: Held(ButtonName.RightBumper),
            Slow: tilt > 0f && tilt < 0.5f,              // levetta appena inclinata: a piedi si va piano
            Sprint: Held(ButtonName.A),                  // ✕ tenuto: a piedi si corre
            Horn: Held(ButtonName.LeftStick),            // L3
            ZoomIn: Held(ButtonName.DPadUp), ZoomOut: Held(ButtonName.DPadDown));
    }

    /// <summary>La levetta destra gira la visuale come farebbe il mouse; nella visuale lontana la ruota attorno all'auto.</summary>
    private void PadLook(Vector2 look, float dt)
    {
        if (look == Vector2.Zero) return;
        if (_onFoot) LookAround(look * PadLookSpeed * dt); // come il mouse: levetta in su = mouse in su
        else if (_farView) _camera.Yaw -= look.X * 1.8f * dt;
    }
}
