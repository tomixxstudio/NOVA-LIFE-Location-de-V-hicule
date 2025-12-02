using System;
using System.Collections.Generic;
using Life;
using Life.Network;
using Life.UI;
using UnityEngine;

namespace NovaLifeLocation
{
    public class VehicleLocationPlugin : Plugin
    {
        // Dictionnaire contenant les locations actives
        private Dictionary<Player, LocationData> activeRentals = new Dictionary<Player, LocationData>();
        
        // Dictionnaire pour gérer les timers individuels
        private Dictionary<Player, System.Threading.Timer> playerTimers = new Dictionary<Player, System.Threading.Timer>();

        // Position du point de location (à personnaliser selon votre map)
        private Vector3 rentalPoint = new Vector3(200f, 5f, 300f);

        // Configuration
        private const int VEHICLE_ID = 44; // ID de la Peugeot 206
        private const int PRICE = 200;
        private const float RENT_DURATION_HOURS = 2f;
        private const float WARNING_AFTER_HOURS = 1f;
        private const float INACTIVITY_LIMIT_MINUTES = 10f;

        public VehicleLocationPlugin(IGameAPI api) : base(api) { }

        public override void OnPluginInit()
        {
            base.OnPluginInit();
            Debug.Log("[NovaLifeLocation] Plugin de location de véhicules initialisé avec succès !");
            
            // Ajoute un marqueur bleu sur la map
            API.AddMarker(rentalPoint, MarkerType.Blue, "Location de véhicules");
            
            Debug.Log($"[NovaLifeLocation] Point de location créé à {rentalPoint}");
        }

        public override void OnPlayerInput(Player player)
        {
            // Vérifier si le joueur est proche du point de location
            if (Vector3.Distance(player.setup.transform.position, rentalPoint) <= 3f)
            {
                OpenRentalMenu(player);
            }
        }

        private void OpenRentalMenu(Player player)
        {
            // Vérifie si le joueur a déjà une location active
            if (activeRentals.ContainsKey(player))
            {
                ShowRenewOrReturnMenu(player);
                return;
            }

            // Menu principal de location
            UIPanel panel = new UIPanel("Location de Véhicules", UIPanel.PanelType.Tab)
                .SetTitle("🚗 LOCATION DE VÉHICULES")
                .AddTabLine($"<color=green>🚘 Louer une Peugeot 206</color>", (ui) =>
                {
                    ui.SelectTab();
                })
                .AddTabLine($"💰 Prix : {PRICE}€ pour 2 heures", (ui) =>
                {
                    ui.SelectTab();
                })
                .AddTabLine("", (ui) => ui.SelectTab())
                .AddButton("✅ Confirmer la location", (ui) =>
                {
                    HandleRentalRequest(player);
                    ui.Close();
                })
                .AddButton("❌ Fermer", (ui) => ui.Close());

            player.ShowPanelUI(panel);
        }

        private void ShowRenewOrReturnMenu(Player player)
        {
            if (!activeRentals.ContainsKey(player)) return;

            var data = activeRentals[player];
            TimeSpan remaining = data.StartTime.AddHours(RENT_DURATION_HOURS) - DateTime.Now;
            int minutesLeft = Math.Max(0, (int)remaining.TotalMinutes);

            UIPanel panel = new UIPanel("Gestion de location", UIPanel.PanelType.Tab)
                .SetTitle("🚗 LOCATION EN COURS")
                .AddTabLine($"⏱️ Temps restant : {minutesLeft} minutes", (ui) => ui.SelectTab())
                .AddTabLine("", (ui) => ui.SelectTab())
                .AddButton($"🔁 Prolonger ({PRICE}€ / 2h)", (ui) =>
                {
                    RenewRental(player);
                    ui.Close();
                })
                .AddButton("🔙 Rendre le véhicule", (ui) =>
                {
                    EndRental(player, "✅ Vous avez rendu le véhicule.");
                    ui.Close();
                })
                .AddButton("❌ Fermer", (ui) => ui.Close());

            player.ShowPanelUI(panel);
        }

        private void HandleRentalRequest(Player player)
        {
            // Vérifie si le joueur a déjà loué un véhicule
            if (activeRentals.ContainsKey(player))
            {
                player.Notify("Location", "❌ Vous avez déjà un véhicule de location actif.", NotificationManager.Type.Error);
                return;
            }

            // Vérifie l'argent
            if (player.character.Money < PRICE)
            {
                player.Notify("Location", "💸 Vous n'avez pas assez d'argent pour louer ce véhicule.", NotificationManager.Type.Error);
                return;
            }

            // Retire l'argent
            player.AddMoney(-PRICE, "Location de véhicule");

            // Calcule la position de spawn (devant le joueur)
            Vector3 spawnPosition = player.setup.transform.position + player.setup.transform.forward * 5f;
            Quaternion spawnRotation = player.setup.transform.rotation;

            // Spawn du véhicule
            Vehicle veh = Nova.v.SpawnVehicle(VEHICLE_ID, spawnPosition, spawnRotation, player.character.Id);

            if (veh == null)
            {
                player.Notify("Location", "❌ Erreur lors du spawn du véhicule.", NotificationManager.Type.Error);
                player.AddMoney(PRICE, "Remboursement location échouée");
                return;
            }

            // Enregistre la location
            activeRentals[player] = new LocationData
            {
                Vehicle = veh,
                StartTime = DateTime.Now,
                LastActivity = DateTime.Now,
                Warned = false
            };

            player.Notify("Location", $"🚘 Vous avez loué une Peugeot 206 pour 2 heures. ({PRICE}€)", NotificationManager.Type.Success);

            // Démarre le suivi de la location
            StartRentalTimer(player);
        }

        private void StartRentalTimer(Player player)
        {
            // Arrête le timer existant s'il y en a un
            if (playerTimers.ContainsKey(player))
            {
                playerTimers[player].Dispose();
                playerTimers.Remove(player);
            }

            // Crée un nouveau timer qui vérifie toutes les 30 secondes
            var timer = new System.Threading.Timer((state) =>
            {
                try
                {
                    // Utilise le thread principal pour les opérations Unity
                    Nova.server.ScheduleOnMainThread(() =>
                    {
                        CheckRentalStatus(player);
                    });
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[NovaLifeLocation] Erreur timer: {ex.Message}");
                }
            }, null, 30000, 30000); // 30 secondes

            playerTimers[player] = timer;
        }

        private void RenewRental(Player player)
        {
            if (!activeRentals.ContainsKey(player))
            {
                player.Notify("Location", "❌ Aucune location active trouvée.", NotificationManager.Type.Error);
                return;
            }

            if (player.character.Money < PRICE)
            {
                player.Notify("Location", "💸 Vous n'avez pas assez d'argent pour prolonger la location.", NotificationManager.Type.Error);
                return;
            }

            player.AddMoney(-PRICE, "Prolongation de location");
            
            var data = activeRentals[player];
            data.StartTime = DateTime.Now; // Réinitialise le timer
            data.Warned = false; // Réinitialise l'avertissement
            data.LastActivity = DateTime.Now;
            
            player.Notify("Location", "🔁 Votre location a été prolongée de 2 heures.", NotificationManager.Type.Success);
        }

        private void CheckRentalStatus(Player player)
        {
            // Vérifie si le joueur est toujours connecté
            if (player == null || !player.IsValid())
            {
                if (activeRentals.ContainsKey(player))
                {
                    CleanupRental(player);
                }
                return;
            }

            if (!activeRentals.ContainsKey(player)) return;

            var data = activeRentals[player];
            TimeSpan sinceStart = DateTime.Now - data.StartTime;
            TimeSpan sinceLastActivity = DateTime.Now - data.LastActivity;

            // Vérifie inactivité (optionnel, peut être commenté si non désiré)
            if (sinceLastActivity.TotalMinutes >= INACTIVITY_LIMIT_MINUTES)
            {
                EndRental(player, "⏱️ Location terminée pour inactivité.");
                return;
            }

            // Avertissement après 1h
            if (!data.Warned && sinceStart.TotalHours >= WARNING_AFTER_HOURS)
            {
                player.Notify("Location", "⚠️ Votre location expire dans 1h. Retournez au point bleu pour prolonger ou rendre le véhicule.", NotificationManager.Type.Warning);
                data.Warned = true;
            }

            // Fin après 2h
            if (sinceStart.TotalHours >= RENT_DURATION_HOURS)
            {
                EndRental(player, "⏰ Votre location est terminée. Le véhicule a été récupéré.");
            }
        }

        private void EndRental(Player player, string reason)
        {
            if (!activeRentals.ContainsKey(player)) return;

            CleanupRental(player);
            
            if (player != null && player.IsValid())
            {
                player.Notify("Location", reason, NotificationManager.Type.Warning);
            }
        }

        private void CleanupRental(Player player)
        {
            if (!activeRentals.ContainsKey(player)) return;

            var data = activeRentals[player];

            // Supprime le véhicule
            if (data.Vehicle != null)
            {
                try
                {
                    data.Vehicle.Kill();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[NovaLifeLocation] Erreur suppression véhicule: {ex.Message}");
                }
            }

            // Nettoie le timer
            if (playerTimers.ContainsKey(player))
            {
                playerTimers[player].Dispose();
                playerTimers.Remove(player);
            }

            // Retire de la liste
            activeRentals.Remove(player);
        }

        public override void OnPlayerMove(Player player)
        {
            if (activeRentals.ContainsKey(player))
            {
                activeRentals[player].LastActivity = DateTime.Now;
            }
        }

        public override void OnPlayerDisconnected(Player player)
        {
            // Nettoie les ressources quand le joueur se déconnecte
            if (activeRentals.ContainsKey(player))
            {
                CleanupRental(player);
            }
        }

        // Nettoyage lors de la destruction du plugin
        public override void OnPluginDestroy()
        {
            // Nettoie tous les timers et locations
            foreach (var timer in playerTimers.Values)
            {
                timer.Dispose();
            }
            playerTimers.Clear();

            foreach (var player in new List<Player>(activeRentals.Keys))
            {
                CleanupRental(player);
            }

            activeRentals.Clear();
            
            base.OnPluginDestroy();
            Debug.Log("[NovaLifeLocation] Plugin de location déchargé proprement.");
        }

        private class LocationData
        {
            public Vehicle Vehicle { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime LastActivity { get; set; }
            public bool Warned { get; set; }
        }
    }
}
