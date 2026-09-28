using System;
using System.Collections;
using BepInEx;
using BepInEx.Configuration;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace LucidCats.TipJar
{
	[BepInPlugin(Guid, Name, Version)]
	public class Plugin : BaseUnityPlugin
	{
		public const string Guid = "io.github.maxmen11.lucidcats.tipjar";
		public const string Name = "TipJar";
		public const string Version = "1.3.2";

		private const string GameSceneName = "GameScene";

		private static readonly Vector3 JarPosition = new Vector3(-4.14499f, 0.8300774f, -5.876164f);

		internal static ConfigEntry<int> TransferAmount;
		internal static ConfigEntry<int> Capacity;
		internal static ConfigEntry<float> DepositFee;
		internal static ConfigEntry<string> WithdrawKey;

		internal static Key ParsedWithdrawKey = Key.Q;

		private TipJarPrefab prefab;
		private NetworkManager registeredOn;
		private TipJarInteractable jar;

		private void Awake()
		{
			TransferAmount = Config.Bind("Economy", "TransferAmount", 100, 
				new ConfigDescription("Credits moved per interaction.", new AcceptableValueRange<int>(1, 1000)));
			Capacity = Config.Bind("Economy", "Capacity", 100000, 
				new ConfigDescription("Max credits the jar holds.", new AcceptableValueRange<int>(1000, 1000000)));
			DepositFee = Config.Bind("Economy", "DepositFee", 0.1f, 
				new ConfigDescription("Extra fraction cost of TransferAmmount on top of each deposit.", new AcceptableValueRange<float>(0f, 3f)));
			WithdrawKey = Config.Bind("Controls", "WithdrawKey", "Q", 
				"Keyboard key that withdraws while hovering the jar. Right Mouse and gamepad East/B also withdraw.");
			ParsedWithdrawKey = Enum.TryParse(WithdrawKey.Value, true, out Key key) ? key : Key.Q;

			TipJarInteractable.OnAnySpawned += HandleJarSpawned;
			StartCoroutine(RegisterPrefabWhenReady());

			Logger.LogInfo($"{Name} v{Version} loaded.");
		}

		private void OnDestroy()
		{
			TipJarInteractable.OnAnySpawned -= HandleJarSpawned;
			NetworkManager nm = NetworkManager.Singleton;

			if (nm != null && nm.IsListening && nm.IsServer && jar != null)
			{
				NetworkObject networkObject = jar.GetComponent<NetworkObject>();
				if (networkObject != null && networkObject.IsSpawned) networkObject.Despawn(true);
			}

			jar = null;
		}

		private void Update()
		{
			NetworkManager nm = NetworkManager.Singleton;

			if (nm == null || !nm.IsListening || !nm.IsServer || nm.ShutdownInProgress || prefab == null) return;

			if (SceneManager.GetActiveScene().name != GameSceneName) return;

			if (jar != null && jar.IsSpawned) return;

			if (jar != null)
			{
				NetworkObject stale = jar.GetComponent<NetworkObject>();
				if (stale != null && stale.IsSpawned) stale.Despawn(true);
				else Destroy(jar.gameObject);
				jar = null;
			}

			prefab.Instantiate(JarPosition, Quaternion.identity).Spawn();
			Logger.LogInfo($"[TipJar] Spawned the tip jar at {JarPosition}.");
		}

		private IEnumerator RegisterPrefabWhenReady()
		{
			var wait = new WaitForSeconds(0.5f);
			while (true)
			{
				var nm = NetworkManager.Singleton;
				if (nm == null || nm == registeredOn)
				{
					yield return wait;
					continue;
				}
				if (prefab == null)
				{
					prefab = TipJarPrefab.CreateAndRegister(nm);
					registeredOn = nm;
					yield return wait;
					continue;
				}
				if (registeredOn != null)
				{
					registeredOn.PrefabHandler.RemoveHandler(prefab.Source);
					registeredOn.RemoveNetworkPrefab(prefab.Source);
				}
				nm.AddNetworkPrefab(prefab.Source);
				nm.PrefabHandler.AddHandler(prefab.Source, prefab);
				registeredOn = nm;
				yield return wait;
			}
		}

		private void HandleJarSpawned(TipJarInteractable spawned)
		{
			jar = spawned;
		}
	}
}