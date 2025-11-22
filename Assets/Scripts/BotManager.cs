using System.Collections.Generic;
using UnityEngine;

public class BotManager : MonoBehaviour
{
    public static BotManager Instance { get; private set; }

    // Qué bot ocupa qué celda de grilla
    private Dictionary<Vector2Int, BotController> occupiedCells = new();

    private System.Random rng = new System.Random();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    public void RegisterBot(BotController bot, Vector2Int gridPos)
    {
        // por si acaso ya había algo, lo sobreescribimos
        occupiedCells[gridPos] = bot;
    }

    // Para que el GridManager pueda tratar bots como obstáculos
    public BotController GetBotAt(Vector2Int gridPos)
    {
        occupiedCells.TryGetValue(gridPos, out var bot);
        return bot;
    }

    /// <summary>
    /// Intenta mover al bot de 'from' a 'to'.
    /// Devuelve true si el movimiento está autorizado.
    /// - Si la celda destino está libre: se actualiza el diccionario y se permite el movimiento.
    /// - Si está ocupada: NO se permite mover a nadie a esa celda en este tick.
    ///   Solo se decide quién debe replantear su ruta (el que tiene MENOR prioridad).
    /// </summary>
    public bool TryMoveWithPriority(BotController bot, Vector2Int from, Vector2Int to)
    {
        // si es la misma celda, no hacemos nada especial
        if (from == to) return true;

        // ¿está libre la celda destino?
        if (!occupiedCells.TryGetValue(to, out BotController other) || other == null)
        {
            // Celda libre -> actualizamos ocupación y dejamos pasar
            if (occupiedCells.TryGetValue(from, out var current) && current == bot)
            {
                occupiedCells.Remove(from);
            }
            occupiedCells[to] = bot;
            return true;
        }

        // Si el otro soy yo mismo, no hay problema
        if (other == bot)
        {
            return true;
        }

        // ----- CONFLICTO: la celda destino está ocupada por otro bot -----

        float myCost = bot.RemainingCost;
        float otherCost = other.RemainingCost;

        // MENOR costo => MÁS prioridad (está más cerca de terminar)
        bool iHaveHigherPriority = false;
        const float epsilon = 0.01f;

        if (myCost + epsilon < otherCost)
        {
            iHaveHigherPriority = true;
        }
        else if (Mathf.Abs(myCost - otherCost) <= epsilon)
        {
            // desempate aleatorio
            iHaveHigherPriority = rng.NextDouble() < 0.5;
        }
        else
        {
            iHaveHigherPriority = false;
        }

        if (iHaveHigherPriority)
        {
            // YO tengo prioridad, entonces el otro es quien debe cambiar su ruta.
            // PERO yo NO entro todavía a su casilla: me espero hasta que se mueva.
            other.ForceReplan();
        }
        else
        {
            // El otro tiene prioridad, yo soy el que debe replantear.
            bot.ForceReplan();
        }

        // En cualquier caso, en este tick NADIE se mueve a 'to'.
        // Eso evita que dos bots se "traspasen" mágicamente.
        return false;
    }
}
