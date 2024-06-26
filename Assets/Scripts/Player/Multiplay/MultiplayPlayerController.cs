﻿using UnityEngine;
using UnityEngine.InputSystem;
using System.Linq;
using Opencraft.Statistics;
using Unity.Entities;
using Unity.Profiling;
using Unity.RenderStreaming;
using Unity.Serialization;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine.UI;

/*
 * Gathers input from Player GameObject
 */
namespace Opencraft.Player.Multiplay
{
    // Collects input either from local devices or a remote input stream using InputActions
    public class MultiplayPlayerController : MonoBehaviour
    {
        
        [SerializeField] InputReceiver playerInput;
        [DontSerialize] public string username;
        [DontSerialize]public Vector2 inputMovement;
        [DontSerialize]public Vector2 inputLook;
        [DontSerialize]public bool inputJump;
        //[DontSerialize]public bool inputStart = false;
        [DontSerialize]public bool inputPrimaryAction = false;
        [DontSerialize]public bool inputSecondaryAction = false;
        [DontSerialize]public bool playerEntityExists;
        [DontSerialize]public bool playerEntityRequestSent;
        [DontSerialize]public Entity playerEntity;
        
        public Text debugText;
        //public Text tooltipText;
        
        private ProfilerRecorder _numAreasRecorder;

        protected void Awake()
        {
            playerInput.onDeviceChange += OnDeviceChange;
            username = $"{PolkaDOTS.Config.UserID}";
        }

        private void OnEnable()
        {
            _numAreasRecorder = ProfilerRecorder.StartNew(GameStatistics.GameStatisticsCategory, GameStatistics.NumTerrainAreasClientName);
        }

        private void OnDestroy()
        {
            _numAreasRecorder.Dispose();
        }

        void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            switch (change)
            {
                case InputDeviceChange.Added:
                {
                    playerInput.PerformPairingWithDevice(device);
                    CheckPairedDevices();
                    return;
                }
                case InputDeviceChange.Removed:
                {
                    playerInput.UnpairDevices(device);
                    CheckPairedDevices();
                    return;
                }
            }
        }

        public void CheckPairedDevices()
        {
            if (!playerInput.user.valid)
                return;

            // todo: touchscreen support
            bool hasTouchscreenDevice =
                playerInput.user.pairedDevices.Count(_ => _.path.Contains("Touchscreen")) > 0;
        }

        private void Update()
        {
            debugText.text = $"NumAreas: {_numAreasRecorder.LastValue}\n";
        }


        public void OnControlsChanged()
        {
        }

        public void OnDeviceLost()
        {
        }

        public void OnDeviceRegained()
        {
        }

        public void OnMovement(InputAction.CallbackContext value)
        {
            inputMovement = value.ReadValue<Vector2>();
        }

        public void OnLook(InputAction.CallbackContext value)
        {
            inputLook = value.ReadValue<Vector2>();
        }

        public void OnJump(InputAction.CallbackContext value)
        {
            if (value.performed)
            {
                inputJump = true;
            }
        }

        /*public void OnStart(InputAction.CallbackContext value)
        {
            if (value.performed)
            {
                inputStart = true;
            }
        }*/
        
        public void OnPrimaryAction(InputAction.CallbackContext value)
        {
            if (value.performed)
            {
                inputPrimaryAction = true;
            }
        }
        
        public void OnSecondaryAction(InputAction.CallbackContext value)
        {
            if (value.performed)
            {
                inputSecondaryAction = true;
            }
        }
        
        public void OnEscapeAction(InputAction.CallbackContext value)
        {
            if (value.performed)
            {
                Debug.Log("Exiting game!");
                //World.DisposeAllWorlds();
#if UNITY_EDITOR
                EditorApplication.ExitPlaymode();
#else
                Application.Quit();
#endif
            }
        }
    }
}

