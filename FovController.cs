using BepInEx;
using Comfort.Common;
using EFT;
using EFT.Animations;
using EFT.CameraControl;
using EFT.InventoryLogic;
using EFT.UI;
using RealismMod;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using static EFT.Player;

namespace FOVFix
{
    //don't remember its purpose...
    public static class WeaponClassExtension
    {
        private static readonly ConditionalWeakTable<Item, StrongBox<string>> _customPropertyValues = new ConditionalWeakTable<Item, StrongBox<string>>();

        public static string GetCustomProperty(this Item instance)
        {
            if (_customPropertyValues.TryGetValue(instance, out var box))
                return box.Value;
            return default;
        }

        public static void SetCustomProperty(this Item instance, string value)
        {
            if (_customPropertyValues.TryGetValue(instance, out var box))
                box.Value = value;
            else
                _customPropertyValues.Add(instance, new StrongBox<string>(value));
        }
    }


    public class ValueWatcher<T>
    {
        private T _watchedValue;
        public T WatchedValue
        {
            get => _watchedValue;
            set
            {
                if (!Equals(_watchedValue, value))
                {
                    _watchedValue = value;
                    OnValueChanged?.Invoke(value);
                }
            }
        }

        public event Action<T> OnValueChanged;
    }

    //to-do: redo everything and put it in here
    public class FovController
    {
        private Player _player = null;
        public AnimatedTextPanel OpticPanel = null;

        public ValueWatcher<bool> ADSWatcher = null;
        ValueWatcher<float> ScopeFOVWatcher = null;
        ValueWatcher<bool> ToggleZoomWatcher = null;
        public ValueWatcher<bool> OpticWatcher = null;

        public Dictionary<string, float> WeaponOffsets = new Dictionary<string, float>();

        public Weapon CurrentWeapon { get; set; }
        public string WeapId { get; set; }
        public float CurrentScopeFOV { get; set; } = 0f;
        public bool IsToggleZoom { get; set; } = false;
        public bool IsPistol { get; set; } = false;
        private bool _wasAiming = false;

        // Debug-only state tracking for the crouch-FOV-glitch investigation.
        private int _lastLoggedPose = int.MinValue;
        private bool _lastLoggedSprint;
        private bool _lastLoggedAiming;
        private bool _lastLoggedFirstPerson;
        private float _lastLoggedZoom = float.NaN;

        public float ScrollCameraOffset 
        {
            get 
            {
                if (CurrentWeapon != null) 
                {
                    string id;
                    if (Utils.IsInHideout)
                    {
                        id = WeaponClassExtension.GetCustomProperty(CurrentWeapon);
                    }
                    else id = WeapId;

                    WeaponOffsets.TryGetValue(id, out float offset);
                    return offset;
                }
   
                return 0f;
            }
            set 
            {
                float scrollCameraOffset = Mathf.Clamp(value, -0.04f, 0.04f);
                string id;
                if (Utils.IsInHideout)
                {
                    id = WeaponClassExtension.GetCustomProperty(CurrentWeapon);
                }
                else id = WeapId;
                if (!id.IsNullOrWhiteSpace()) WeaponOffsets[id] = scrollCameraOffset;
            }
        }

        public FovController()
        {
            ADSWatcher = new ValueWatcher<bool>();
            ADSWatcher.OnValueChanged += newValue => ChangeMainCamFOV();
            ScopeFOVWatcher = new ValueWatcher<float>();
            ScopeFOVWatcher.OnValueChanged += newValue => HandleScopeFOVChange();
            ToggleZoomWatcher = new ValueWatcher<bool>();
            ToggleZoomWatcher.OnValueChanged += newValue => HandleToggleZoomChange();
            OpticWatcher = new ValueWatcher<bool>();
            OpticWatcher.OnValueChanged += newValue => HandleToggleZoomChange();
        }

        public void ChangeMainCamFOV()
        {
            if (IsPWANull()) return;
            var pwa = _player.ProceduralWeaponAnimation;
            var fc = _player.HandsController as FirearmController;

            // Ported note: original read obfuscated "Single_2". Live 4.1.3's ProceduralWeaponAnimation
            // exposes this exact value (Settings.Game.FieldOfView.Value) as "HeadBobbing" - a
            // misleading name, but it's the same property the engine itself feeds into
            // CameraManager.SetFov for base FOV (confirmed at ProceduralWeaponAnimation line ~645).
            float baseFOV = _player.ProceduralWeaponAnimation.HeadBobbing;
            int aimIndex = _player.ProceduralWeaponAnimation.AimIndex;
            float toggleZoomMulti = 1f;
            float magnificationModifier = 1f;


            if (fc == null || !pwa.IsAiming)
            {
                if (_wasAiming && Plugin.CancelToggleOnUnADS.Value) IsToggleZoom = false;
                else toggleZoomMulti = IsToggleZoom ? Plugin.UnaimedToggleZoomMulti.Value : 1f;
            }

            bool fcIsNull = fc != null && fc?.Weapon != null;
            if (fcIsNull && pwa.PointOfView == EPointOfView.FirstPerson && !pwa.Sprint && pwa.IsAiming && aimIndex < pwa.ScopeAimTransforms.Count)
            {
                toggleZoomMulti = !IsToggleZoom ? 1f : OpticWatcher.WatchedValue ? Plugin.OpticToggleZoomMulti.Value : Plugin.NonOpticToggleZoomMulti.Value;
                magnificationModifier = OpticWatcher.WatchedValue ? Plugin.GlobalADSMulti.Value : Plugin.NonOpticFOVMulti.Value; //in future once scope FOV is standardized, will need to use current scope FOV to figure out what equiivalent scope mag it is, and use that to modify main cam FOV
            }

            float zoom = baseFOV * magnificationModifier * toggleZoomMulti;
            CameraManager.Instance.SetFov(zoom, 1f, !pwa.IsAiming);
            _wasAiming = pwa.IsAiming;

            Utils.DLogThrottled("ChangeMainCamFOV", $"ChangeMainCamFOV: pose={pwa.Pose} sprint={pwa.Sprint} aiming={pwa.IsAiming} pov={pwa.PointOfView} leftStance={pwa.LeftStance} baseFOV={baseFOV:F2} magMod={magnificationModifier:F3} toggleZoomMulti={toggleZoomMulti:F3} zoom={zoom:F2} weapon={CurrentWeapon?.Name} isPistol={IsPistol} isToggleZoom={IsToggleZoom}", 0.5f);

            // Spike detector: logs unconditionally (no throttle) the instant zoom jumps more than
            // 3 degrees in a single call, so a one-frame glitch isn't hidden by the 0.5s throttle above.
            if (!float.IsNaN(_lastLoggedZoom) && Mathf.Abs(zoom - _lastLoggedZoom) > 3f)
            {
                Utils.DLog($"!!! FOV SPIKE: {_lastLoggedZoom:F2} -> {zoom:F2} (delta={zoom - _lastLoggedZoom:F2}) pose={pwa.Pose} sprint={pwa.Sprint} aiming={pwa.IsAiming} weapon={CurrentWeapon?.Name}");
            }
            _lastLoggedZoom = zoom;
        }

        public bool IsPWANull()
        {
            ProceduralWeaponAnimation pwa = _player.ProceduralWeaponAnimation;
            return pwa == null;
        }

        public void HandleScopeFOVChange() 
        {
            ChangeMainCamFOV();
        }

        public void HandleToggleZoomChange()
        {
            ChangeMainCamFOV();
        }

        public void HandleScopeChange()
        {
            ChangeMainCamFOV();
        }

        public void CheckAiming() 
        {
            if (IsPWANull()) return;
            ADSWatcher.WatchedValue = _player.ProceduralWeaponAnimation.IsAiming;
        }

        public void CheckScopeFOV()
        {
            if (IsPWANull()) return;
            ScopeFOVWatcher.WatchedValue = CurrentScopeFOV;
        }

        public void CheckScope()
        {
            if (IsPWANull() || !Utils.WeaponIsReady) return;
            var pwa = _player.ProceduralWeaponAnimation;
            if (pwa.AimIndex < pwa.ScopeAimTransforms.Count) OpticWatcher.WatchedValue = pwa.CurrentScope.IsOptic;
        }

        public void CheckToggleZoom()
        {
            ToggleZoomWatcher.WatchedValue = IsToggleZoom;
        }

        public void UpdateToggleZoom()
        {
            if (Plugin.ToggleZoomOnHoldBreath.Value) DoHoldBreahtToggleZoom();
            else DoKeybindToggleZoom();
        }

        public void DoHoldBreahtToggleZoom() 
        {
            if (_player.Physical.HoldingBreath) IsToggleZoom = true;
            else if (!_player.Physical.HoldingBreath) IsToggleZoom = false;
        }

        public void DoKeybindToggleZoom() 
        {
            if (Plugin.HoldToggleZoom.Value)
            {
                if (Input.GetKey(Plugin.ToggleZoomKeybind.Value.MainKey) && Plugin.ToggleZoomKeybind.Value.Modifiers.All(Input.GetKey)) IsToggleZoom = true;
                if (!Input.GetKey(Plugin.ToggleZoomKeybind.Value.MainKey)) IsToggleZoom = false;
            }
            else if (Input.GetKeyDown(Plugin.ToggleZoomKeybind.Value.MainKey) && Plugin.ToggleZoomKeybind.Value.Modifiers.All(Input.GetKey)) IsToggleZoom = !IsToggleZoom;
        }

        public void UpateScopeFOV()
        {
            if (CameraManager.Instance?.OpticCameraManager?.Camera != null) CurrentScopeFOV = CameraManager.Instance.OpticCameraManager.Camera.fieldOfView;
        }


        private void HandleCameraOffsetInput()
        {
            if (Input.GetKey(Plugin.CameraIncreaseOffset.Value.MainKey) && Plugin.CameraIncreaseOffset.Value.Modifiers.All(Input.GetKey))
            {
                ScrollCameraOffset += 0.0005f;
            }
            if (Input.GetKey(Plugin.CameraDecreaseOffset.Value.MainKey) && Plugin.CameraDecreaseOffset.Value.Modifiers.All(Input.GetKey))
            {
                ScrollCameraOffset -= 0.0005f;
            }
        }

        public void ControllerUpdate()
        {
            Utils.CheckIsReady();
            if (!Utils.PlayerIsReady) return;

            if (_player == null) _player = Singleton<GameWorld>.Instance.MainPlayer;
            if (_player != null)
            {
                HandleCameraOffsetInput();
                UpateScopeFOV();
                UpdateToggleZoom();
                CheckAiming();
                CheckScopeFOV();
                CheckToggleZoom();
                CheckScope();
                LogStateChangesForDebug();
            }
        }

        // Edge-triggered: fires the instant pose/sprint/aiming/view actually changes, so a crouch
        // transition lands at an exact log line instead of being buried in the throttled per-frame log.
        private void LogStateChangesForDebug()
        {
            if (IsPWANull()) return;
            var pwa = _player.ProceduralWeaponAnimation;

            if (pwa.Pose != _lastLoggedPose)
            {
                Utils.DLog($"POSE CHANGED: {_lastLoggedPose} -> {pwa.Pose} (aiming={pwa.IsAiming} sprint={pwa.Sprint} pov={pwa.PointOfView} weapon={CurrentWeapon?.Name})");
                _lastLoggedPose = pwa.Pose;
            }
            if (pwa.Sprint != _lastLoggedSprint)
            {
                Utils.DLog($"SPRINT CHANGED: {_lastLoggedSprint} -> {pwa.Sprint} (pose={pwa.Pose} aiming={pwa.IsAiming})");
                _lastLoggedSprint = pwa.Sprint;
            }
            if (pwa.IsAiming != _lastLoggedAiming)
            {
                Utils.DLog($"AIMING CHANGED: {_lastLoggedAiming} -> {pwa.IsAiming} (pose={pwa.Pose} sprint={pwa.Sprint})");
                _lastLoggedAiming = pwa.IsAiming;
            }
            bool isFirstPerson = pwa.PointOfView == EPointOfView.FirstPerson;
            if (isFirstPerson != _lastLoggedFirstPerson)
            {
                Utils.DLog($"POV CHANGED: firstPerson={isFirstPerson} (pose={pwa.Pose})");
                _lastLoggedFirstPerson = isFirstPerson;
            }
        }
    }
}
