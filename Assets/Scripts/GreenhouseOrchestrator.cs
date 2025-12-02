using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Orquestador general del invernadero.
///
/// Fases:
/// 1) Espera a que TODOS los ScoutBotBrain completen su misión.
/// 2) Usa TomatoFieldManager (appearsSuspicious / isTrulySick)
///    para separar plantas sospechosas, sanas y realmente enfermas.
///    - Lanza NurseBots sobre las sospechosas.
/// 3) Espera a que TODOS los NurseBotBrain terminen.
/// 4) Lanza PickBots (BotController) solo sobre las plantas sanas.
/// </summary>
public class GreenhouseOrchestrator : MonoBehaviour
{
    [Header("General")]
    [Tooltip("Si está activo, corre el escenario automáticamente al hacer Play.")]
    public bool autoStart = true;

    [Tooltip("Delay inicial para asegurarnos de que todo esté inicializado (scouts, grid, etc.).")]
    public float startDelay = 1.0f;

    // Listas que el "patólogo" va a construir
    private List<TomatoFieldManager.TomatoTask> suspiciousTasks;
    private List<TomatoFieldManager.TomatoTask> healthyTasks;
    private List<TomatoFieldManager.TomatoTask> trulySickTasks;

    private void Start()
    {
        if (autoStart)
        {
            StartCoroutine(RunScenario());
        }
    }

    /// <summary>
    /// Corrutina principal del escenario:
    /// 1) Detecta scouts y espera a que terminen.
    /// 2) Fase del patólogo + arranque de NurseBots.
    /// 3) Espera NurseBots -> arranca PickBots.
    /// </summary>
    private IEnumerator RunScenario()
    {
        // Espera inicial para que GridManager, Scouts, MissionController, etc. estén listos
        yield return new WaitForSeconds(startDelay);

        // 1. Detectar scouts en escena
        ScoutBotBrain[] scouts = FindObjectsOfType<ScoutBotBrain>();
        if (scouts.Length == 0)
        {
            Debug.LogWarning("[Orchestrator] No se encontraron Scouts (ScoutBotBrain).");
            yield break;
        }

        Debug.Log($"[Orchestrator] Detectados {scouts.Length} scouts. Iniciando fase de exploración...");

        // 2. Esperar a que todos reporten MissionComplete = true
        bool allScoutsDone = false;

        while (!allScoutsDone)
        {
            allScoutsDone = true;

            foreach (var scout in scouts)
            {
                if (scout == null) continue;

                if (!scout.MissionComplete)
                {
                    allScoutsDone = false;
                    break;
                }
            }

            if (!allScoutsDone)
            {
                // Espera 1 frame antes de volver a checar
                yield return null;
            }
        }

        Debug.Log("[Orchestrator] TODOS los scouts completaron su misión. Iniciando diagnóstico del patólogo...");

        // 3. Fase del Patólogo (clasifica plantas y arranca NurseBots)
        StartPathologistPhase();

        // 4. Esperar a que NurseBots terminen y luego lanzar cosecha sana
        yield return StartCoroutine(WaitForNurseBotsAndLaunchHarvest());
    }

    /// <summary>
    /// Fase del Patólogo:
    /// - Toma todas las TomatoTask del campo desde TomatoFieldManager.
    /// - Usa appearsSuspicious / isTrulySick para armar las listas:
    ///   * suspiciousTasks: todas las sospechosas (verde).
    ///   * healthyTasks: todas las NO sospechosas (para PickBots sanos).
    ///   * trulySickTasks: todas las realmente enfermas (stats / lógica futura).
    /// - Lanza NurseBots sobre suspiciousTasks.
    /// </summary>
    private void StartPathologistPhase()
    {
        var field = TomatoFieldManager.Instance;
        if (field == null)
        {
            Debug.LogWarning("[Orchestrator/Pathologist] No hay TomatoFieldManager en la escena.");
            return;
        }

        field.EnsureTasksBuilt();
        List<TomatoFieldManager.TomatoTask> tasks = field.allTasks;

        if (tasks == null || tasks.Count == 0)
        {
            Debug.LogWarning("[Orchestrator/Pathologist] No hay tareas de cosecha generadas.");
            return;
        }

        suspiciousTasks = new List<TomatoFieldManager.TomatoTask>();
        healthyTasks = new List<TomatoFieldManager.TomatoTask>();
        trulySickTasks = new List<TomatoFieldManager.TomatoTask>();

        foreach (var t in tasks)
        {
            if (t.appearsSuspicious)
            {
                // Esta planta fue marcada visualmente como sospechosa (verde)
                suspiciousTasks.Add(t);

                if (t.isTrulySick)
                {
                    // De las sospechosas, algunas realmente enfermas
                    trulySickTasks.Add(t);
                }
            }
            else
            {
                // No fue marcada como sospechosa -> la consideramos "sana" para cosecha normal
                healthyTasks.Add(t);
            }
        }

        int total = tasks.Count;
        int suspiciousCount = suspiciousTasks.Count;
        int healthyCount = healthyTasks.Count;
        int trulySickCount = trulySickTasks.Count;

        Debug.Log(
            $"[Pathologist] Evaluadas {total} plantas. " +
            $"Sospechosas={suspiciousCount}, " +
            $"Realmente enfermas={trulySickCount}, " +
            $"Sanas (no sospechosas)={healthyCount}."
        );

        // 🔹 Aquí arrancamos la fase de NurseBots con las sospechosas
        StartNurseBotsPhase();
    }

    /// <summary>
    /// Reparte las plantas sospechosas entre todos los NurseBotBrain de la escena
    /// y arranca su misión.
    /// </summary>
    private void StartNurseBotsPhase()
    {
        NurseBotBrain[] nurses = FindObjectsOfType<NurseBotBrain>();
        if (nurses.Length == 0)
        {
            Debug.LogWarning("[Orchestrator] No se encontraron NurseBots en la escena.");
            return;
        }

        if (suspiciousTasks == null || suspiciousTasks.Count == 0)
        {
            Debug.Log("[Orchestrator] No hay plantas sospechosas. NurseBots no tienen trabajo.");
            return;
        }

        Debug.Log($"[Orchestrator] Asignando {suspiciousTasks.Count} plantas sospechosas a {nurses.Length} NurseBots...");

        int total = suspiciousTasks.Count;
        int numNurses = nurses.Length;

        int baseCount = total / numNurses;
        int remainder = total % numNurses;
        int index = 0;

        for (int i = 0; i < numNurses; i++)
        {
            int count = baseCount + (i < remainder ? 1 : 0);

            List<TomatoFieldManager.TomatoTask> slice;
            if (count > 0)
            {
                slice = suspiciousTasks.GetRange(index, count);
                index += count;
            }
            else
            {
                slice = new List<TomatoFieldManager.TomatoTask>();
            }

            nurses[i].AssignTasks(slice);
            nurses[i].StartMission();  // 🔹 aquí arrancan de verdad

            Debug.Log(
                $"[Orchestrator] NurseBot {nurses[i].name} recibe {slice.Count} plantas sospechosas."
            );
        }
    }

    /// <summary>
    /// Espera a que todos los NurseBots terminen (MissionComplete)
    /// y luego lanza la fase de cosecha sana con los BotController.
    /// </summary>
    private IEnumerator WaitForNurseBotsAndLaunchHarvest()
    {
        NurseBotBrain[] nurses = FindObjectsOfType<NurseBotBrain>();

        bool nursesHaveWork =
            nurses.Length > 0 &&
            suspiciousTasks != null &&
            suspiciousTasks.Count > 0;

        if (nursesHaveWork)
        {
            Debug.Log("[Orchestrator] Esperando a que todos los NurseBots terminen su misión...");

            bool allDone = false;
            while (!allDone)
            {
                allDone = true;

                foreach (var nurse in nurses)
                {
                    if (nurse == null) continue;

                    if (!nurse.MissionComplete)
                    {
                        allDone = false;
                        break;
                    }
                }

                if (!allDone)
                    yield return null;
            }

            Debug.Log("[Orchestrator] TODOS los NurseBots completaron su misión. Iniciando cosecha de plantas sanas...");
        }
        else
        {
            Debug.Log("[Orchestrator] No hay NurseBots o no hay plantas sospechosas. Pasando directo a cosecha sana...");
        }

        StartHarvestPhase();
    }

    /// <summary>
    /// Distribuye las plantas sanas (healthyTasks) entre todos los BotController
    /// y arranca su misión de cosecha.
    /// </summary>
    private void StartHarvestPhase()
    {
        if (healthyTasks == null || healthyTasks.Count == 0)
        {
            Debug.LogWarning("[Orchestrator] No hay plantas sanas (healthyTasks) para cosechar.");
            return;
        }

        BotController[] pickBots = FindObjectsOfType<BotController>();
        if (pickBots.Length == 0)
        {
            Debug.LogWarning("[Orchestrator] No se encontraron BotController (pickbots) en la escena.");
            return;
        }

        Debug.Log($"[Orchestrator] Asignando {healthyTasks.Count} plantas sanas a {pickBots.Length} pickbots...");

        int total = healthyTasks.Count;
        int numBots = pickBots.Length;

        int baseCount = total / numBots;
        int remainder = total % numBots;
        int index = 0;

        for (int i = 0; i < numBots; i++)
        {
            int count = baseCount + (i < remainder ? 1 : 0);

            List<TomatoFieldManager.TomatoTask> slice;
            if (count > 0)
            {
                slice = healthyTasks.GetRange(index, count);
                index += count;
            }
            else
            {
                slice = new List<TomatoFieldManager.TomatoTask>();
            }

            pickBots[i].SetAssignedTasks(slice);
            pickBots[i].StartMission();

            Debug.Log(
                $"[Orchestrator] PickBot {pickBots[i].name} recibe {slice.Count} plantas sanas."
            );
        }
    }
}
