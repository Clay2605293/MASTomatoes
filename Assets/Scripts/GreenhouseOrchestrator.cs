using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Orquestador general del invernadero.
///
/// Fases:
/// 1) Espera a que TODOS los ScoutBotBrain completen su misión.
/// 2) Usa TomatoFieldManager (appearsSuspicious) para separar plantas sospechosas y sanas,
///    y deja marcada internamente la verdad (isTrulySick) para métricas.
///    - Lanza NurseBots sobre las sospechosas (recogen muestras y las analizan).
/// 3) Espera a que TODOS los NurseBotBrain terminen (ahí es cuando el sistema
///    "sabe" realmente cuáles están enfermas) y en ese momento:
///      - Enciende la alerta visual
///      - Coloca flechas rojas sobre las plantas realmente enfermas
/// 4) Lanza PickBots (BotController) solo sobre las plantas sanas.
/// 5) Espera a que TODOS los pickbots terminen y genera un reporte resumido.
/// </summary>
public class GreenhouseOrchestrator : MonoBehaviour
{
    [Header("General")]
    [Tooltip("Si está activo, corre el escenario automáticamente al hacer Play.")]
    public bool autoStart = true;

    [Tooltip("Delay inicial para asegurarnos de que todo esté inicializado (scouts, grid, etc.).")]
    public float startDelay = 1.0f;

    [Header("Alertas visuales")]
    [Tooltip("Objeto que se encenderá cuando se detecten plantas realmente enfermas (sirena, luz, etc.).")]
    public GameObject diseaseAlertObject;

    [Tooltip("Luz que cambiará de color/estado cuando haya plantas enfermas.")]
    public Light diseaseAlertLight;

    public Color healthyColor = Color.green;
    public Color alertColor = Color.red;

    [Header("Diagnóstico (solo lectura)")]
    public int totalPlants;
    public int suspiciousPlants;
    public int healthyPlants;
    public int trulySickPlants;

    public int totalTomatoes;
    public int suspiciousTomatoes;
    public int healthyTomatoes;
    public int sickTomatoes; // tomates en plantas realmente enfermas

    [Header("Tiempos (segundos, solo lectura)")]
    public float scoutsDuration;
    public float pathologistDuration;
    public float nursesDuration;
    public float harvestDuration;
    public float totalScenarioDuration;

    // Listas que el "patólogo" va a construir
    private List<TomatoFieldManager.TomatoTask> suspiciousTasks;
    private List<TomatoFieldManager.TomatoTask> healthyTasks;
    private List<TomatoFieldManager.TomatoTask> trulySickTasks;

    // Marcadores internos de tiempo
    private float scenarioStartTime;
    private float scoutsEndTime;
    private float pathologistStartTime;
    private float pathologistEndTime;
    private float nurseStartTime;
    private float nurseEndTime;
    private float harvestStartTime;
    private float harvestEndTime;

    private void Start()
    {
        // Limpiar cualquier marcador viejo de otra corrida
        if (TomatoFieldManager.Instance != null)
        {
            TomatoFieldManager.Instance.ClearTrulySickMarkers();
        }

        // Aseguramos que la alerta arranque en estado "sin problemas"
        TriggerDiseaseAlert(false);

        if (autoStart)
        {
            StartCoroutine(RunScenario());
        }
    }

    /// <summary>
    /// Corrutina principal del escenario:
    /// 1) Detecta scouts y espera a que terminen.
    /// 2) Fase del patólogo + arranque de NurseBots.
    /// 3) Espera NurseBots -> enciende alerta + flechas -> arranca PickBots -> espera cosecha.
    /// 4) Genera reporte final.
    /// </summary>
    private IEnumerator RunScenario()
    {
        // Espera inicial para que GridManager, Scouts, MissionController, etc. estén listos
        yield return new WaitForSeconds(startDelay);

        scenarioStartTime = Time.time;

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

        scoutsEndTime = Time.time;
        scoutsDuration = scoutsEndTime - scenarioStartTime;

        Debug.Log("[Orchestrator] TODOS los scouts completaron su misión. Iniciando diagnóstico del patólogo...");

        // 3. Fase del Patólogo (clasifica plantas y arranca NurseBots)
        pathologistStartTime = Time.time;
        StartPathologistPhase();
        pathologistEndTime = Time.time;
        pathologistDuration = pathologistEndTime - pathologistStartTime;

        // 4. Esperar a que NurseBots terminen y luego lanzar cosecha sana + esperar pickbots
        yield return StartCoroutine(WaitForNursesAndHarvest());

        // 5. Calcular tiempos finales y generar reporte
        harvestEndTime = Time.time;
        harvestDuration = harvestEndTime - harvestStartTime;
        totalScenarioDuration = harvestEndTime - scenarioStartTime;

        GenerateFinalReport();
    }

    /// <summary>
    /// Fase del Patólogo:
    /// - Toma todas las TomatoTask del campo desde TomatoFieldManager.
    /// - Usa appearsSuspicious para armar:
    ///   * suspiciousTasks: todas las sospechosas (para NurseBots).
    ///   * healthyTasks: todas las NO sospechosas (para PickBots sanos).
    /// - También registra internamente qué plantas son realmente enfermas (isTrulySick),
    ///   pero esa info solo se usa "hacia afuera" después del análisis de NurseBots.
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

        // Reset stats
        totalPlants = tasks.Count;
        suspiciousPlants = 0;
        healthyPlants = 0;
        trulySickPlants = 0;

        totalTomatoes = 0;
        suspiciousTomatoes = 0;
        healthyTomatoes = 0;
        sickTomatoes = 0;

        foreach (var t in tasks)
        {
            // Conteo de tomates (tal como fueron generados al inicio)
            totalTomatoes += t.tomatoes;

            if (t.appearsSuspicious)
            {
                suspiciousTasks.Add(t);
                suspiciousPlants++;
                suspiciousTomatoes += t.tomatoes;

                // VERDAD INTERNA: solo para métricas y para flechas posteriores
                if (t.isTrulySick)
                {
                    trulySickTasks.Add(t);
                    trulySickPlants++;
                    sickTomatoes += t.tomatoes;
                }
            }
            else
            {
                healthyTasks.Add(t);
                healthyPlants++;
                healthyTomatoes += t.tomatoes;
            }
        }

        Debug.Log(
            $"[Pathologist] Evaluadas {totalPlants} plantas. " +
            $"Sospechosas={suspiciousPlants}, " +
            $"Sanas (no sospechosas)={healthyPlants}. " +
            "(Resultado de laboratorio aún pendiente)"
        );

        // En esta fase aún no prendemos alerta ni flechas:
        // el laboratorio (NurseBots/ENF) todavía no ha confirmado.
        TriggerDiseaseAlert(false);

        // Arrancamos la fase de NurseBots con las sospechosas
        StartNurseBotsPhase();
    }

    /// <summary>
    /// Control de la alerta visual / luz de enfermedad.
    /// </summary>
    private void TriggerDiseaseAlert(bool hasDisease)
    {
        if (diseaseAlertObject != null)
        {
            diseaseAlertObject.SetActive(hasDisease);
        }

        if (diseaseAlertLight != null)
        {
            diseaseAlertLight.enabled = hasDisease;
            diseaseAlertLight.color = hasDisease ? alertColor : healthyColor;
        }

        if (hasDisease)
        {
            Debug.Log("[Orchestrator] ALERTA: Se han confirmado plantas realmente enfermas en el invernadero.");
        }
        else
        {
            Debug.Log("[Orchestrator] Estado del invernadero: sin plantas enfermas confirmadas.");
        }
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
            nurses[i].StartMission();

            Debug.Log(
                $"[Orchestrator] NurseBot {nurses[i].name} recibe {slice.Count} plantas sospechosas."
            );
        }

        nurseStartTime = Time.time;
    }

    /// <summary>
    /// Espera a que todos los NurseBots terminen (MissionComplete).
    /// Al terminar:
    ///  - Enciende alerta si hay trulySickPlants
    ///  - Pide a TomatoFieldManager que ponga flechas rojas en las plantas enfermas
    /// Luego lanza la fase de cosecha sana con los BotController
    /// y espera también a que terminen para poder generar el reporte final.
    /// </summary>
    private IEnumerator WaitForNursesAndHarvest()
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

            nurseEndTime = Time.time;
            nursesDuration = nurseEndTime - nurseStartTime;

            // AHORA sí: después del análisis de las muestras (fase Nurse + ENF)
            // el sistema "conoce" el resultado real.
            bool hasDisease = trulySickPlants > 0;

            // 1) Encender alerta visual
            TriggerDiseaseAlert(hasDisease);

            // 2) Colocar flechas rojas sobre las plantas realmente enfermas
            if (hasDisease && TomatoFieldManager.Instance != null &&
                trulySickTasks != null && trulySickTasks.Count > 0)
            {
                TomatoFieldManager.Instance.ShowTrulySickMarkers(trulySickTasks);
            }

            Debug.Log(
                $"[Orchestrator] Resultado de laboratorio: {trulySickPlants} plantas realmente enfermas " +
                $"de {suspiciousPlants} sospechosas."
            );

            Debug.Log("[Orchestrator] TODOS los NurseBots completaron su misión. Iniciando cosecha de plantas sanas...");
        }
        else
        {
            // No hubo trabajo para nurses -> no hay diagnóstico de laboratorio
            nurseStartTime = nurseEndTime = Time.time;
            nursesDuration = 0f;

            Debug.Log("[Orchestrator] No hay NurseBots o no hay plantas sospechosas. Pasando directo a cosecha sana...");
        }

        // Lanzar fase de cosecha sana
        harvestStartTime = Time.time;
        StartHarvestPhase();

        // Esperar a que terminen los pickbots
        yield return StartCoroutine(WaitForPickBotsToFinish());
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

    /// <summary>
    /// Espera a que todos los BotController terminen su misión (MissionComplete).
    /// </summary>
    private IEnumerator WaitForPickBotsToFinish()
    {
        BotController[] pickBots = FindObjectsOfType<BotController>();

        bool botsHaveWork =
            pickBots.Length > 0 &&
            healthyTasks != null &&
            healthyTasks.Count > 0;

        if (!botsHaveWork)
        {
            Debug.Log("[Orchestrator] No hay pickbots o no hay plantas sanas asignadas. No se realiza cosecha.");
            yield break;
        }

        Debug.Log("[Orchestrator] Esperando a que todos los pickbots terminen la cosecha...");

        bool allDone = false;

        while (!allDone)
        {
            allDone = true;

            foreach (var bot in pickBots)
            {
                if (bot == null) continue;

                if (!bot.MissionComplete)
                {
                    allDone = false;
                    break;
                }
            }

            if (!allDone)
                yield return null;
        }

        Debug.Log("[Orchestrator] TODOS los pickbots completaron la cosecha de plantas sanas.");
    }

    /// <summary>
    /// Genera un reporte legible para el dueño del invernadero
    /// con tiempos y métricas de la jornada.
    /// </summary>
    private void GenerateFinalReport()
    {
        // Métricas de calidad del diagnóstico
        float suspiciousRate = totalPlants > 0
            ? (float)suspiciousPlants / totalPlants * 100f
            : 0f;

        float sickRate = totalPlants > 0
            ? (float)trulySickPlants / totalPlants * 100f
            : 0f;

        float precisionSuspicious = suspiciousPlants > 0
            ? (float)trulySickPlants / suspiciousPlants * 100f
            : 0f;

        string report =
            "================= REPORTE DEL INVERNADERO =================\n" +
            $"Plantas totales evaluadas:       {totalPlants}\n" +
            $" - Plantas sospechosas:          {suspiciousPlants} ({suspiciousRate:F1}% del total)\n" +
            $" - Plantas realmente enfermas:   {trulySickPlants} ({sickRate:F1}% del total)\n" +
            $" - Plantas sanas (no sospechosas): {healthyPlants}\n" +
            $" - Precisión del sistema de sospecha: {precisionSuspicious:F1}% de las sospechosas estaban enfermas\n" +
            "\n" +
            $"Tomates totales en el campo:     {totalTomatoes}\n" +
            $" - Tomates en plantas sospechosas: {suspiciousTomatoes}\n" +
            $" - Tomates en plantas realmente enfermas: {sickTomatoes}\n" +
            $" - Tomates en plantas sanas:     {healthyTomatoes}\n" +
            "\n" +
            "Tiempos de la jornada (segundos):\n" +
            $" - Exploración (scouts):         {scoutsDuration:F2} s\n" +
            $" - Diagnóstico del patólogo:     {pathologistDuration:F2} s\n" +
            $" - Muestreo/Análisis (NurseBots): {nursesDuration:F2} s\n" +
            $" - Cosecha de plantas sanas:     {harvestDuration:F2} s\n" +
            $" - Tiempo total del escenario:   {totalScenarioDuration:F2} s\n" +
            "============================================================";

        Debug.Log(report);
    }
}
