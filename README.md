# Sistema Multiagente para Detección Temprana de ToBRFV en Invernadero Simulado

Este repositorio contiene la propuesta y la implementación de un **sistema multiagente** en un **Digital Environment** (invernadero simulado en Unity) para apoyar la **detección temprana del Tomato brown rugose fruit virus (ToBRFV)**, separando el manejo de frutos sanos, sospechosos e infectados mediante agentes especializados.

El proyecto forma parte del bloque de Sistemas Multiagentes y se centra en el diseño conceptual, arquitectónico y la simulación de los agentes involucrados.

---

## Descripción general del reto

En la operación tradicional de invernaderos, la detección de posibles infecciones se realiza de forma manual y reactiva:

1. Un trabajador identifica síntomas sospechosos.
2. Informa al capataz.
3. Se consulta a un especialista.
4. La respuesta es tardía: suele implicar eliminar plantas o secciones completas.

Este proceso es:
- **Lento**, dependiente de múltiples intermediarios.
- **Subjetivo**, dependiente del ojo humano.
- **Costoso**, por la posible pérdida de producción sana.

Este proyecto propone un **entorno simulado** donde un conjunto de agentes autónomos colabora para:
- Recorrer sistemáticamente el invernadero.
- Capturar información visual de plantas y frutos.
- Centralizar el diagnóstico en un agente inteligente.
- Coordinar la recolección diferenciada de producción sana y muestras sospechosas.

---

## Enfoque general de la solución

Se modela un invernadero como un **grafo de segmentos** (nodos = segmentos de filas con plantas asociadas; aristas = pasillos).  
Sobre este entorno trabajan los siguientes agentes:

- **Patólogo Digital (PathologistAgent)** – *Agente híbrido*  
  Cerebro central del sistema. Conoce el grafo completo, asigna misiones, procesa imágenes, realiza diagnóstico en dos etapas, coordina PickBots y genera alertas.

- **ScoutBots (Agentes Exploradores)** – *Agentes reactivos*  
  Ejecutan misiones de exploración definidas por el Patólogo. Recorren segmentos, capturan imágenes de plantas y frutos, almacenan datos localmente y los descargan en la Base central al finalizar.

- **PickBot Enfermero** – *Agente reactivo especializado*  
  Visita plantas con frutos sospechosos, recolecta muestras y las lleva al área de análisis del Patólogo para una evaluación detallada.

- **PickBot de Sanos** (opcional) – *Agente reactivo de cosecha segura*  
  Recolecta únicamente en zonas confirmadas como seguras, manteniendo separado el flujo de producción sana del manejo de sospechosos.

Toda la inteligencia de diagnóstico y planificación global se concentra en el **Patólogo Digital**, siguiendo el enfoque de **“robots simples + cerebro central”**.

---

## Arquitectura de agentes (resumen)

### Patólogo Digital (Híbrido)
- **Capa Reactiva:** Maneja eventos inmediatos (misiones completadas, muestras entregadas, estados de segmentos).
- **Capa Deliberativa (BDI):**
  - Creencias: grafo del invernadero, estado de segmentos, plantas, frutos, recursos y resultados de análisis.
  - Deseos: máxima cobertura, detección temprana, minimizar falsos negativos, proteger producción sana.
  - Intenciones: planificación de misiones, screening masivo, gestión de sospechosos, análisis detallado, activación de PickBots, reinspección.

### ScoutBots (Reactivos)
- Reciben lista de segmentos.
- Recorren, capturan imágenes y registran resultados.
- Saltan segmentos bloqueados sin recalcular rutas globales.
- Regresan a Base y descargan datos al Patólogo.

### PickBot Enfermero (Reactivo)
- Recibe lista de frutos sospechosos.
- Recolecta muestras.
- Entrega en Base para análisis detallado del Patólogo.

### PickBot de Sanos (Reactivo)
- Recibe zonas seguras.
- Cosecha únicamente en áreas confirmadas como no infectadas.
- Mantiene separado el flujo de producción sana.

La especificación completa de cada agente se encuentra en la documentación del proyecto.

