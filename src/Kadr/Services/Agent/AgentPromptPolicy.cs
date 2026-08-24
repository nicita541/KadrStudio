namespace KadrStudio.Services.Agent;

/// <summary>
/// Canonical model-facing rules shared by investigation, planning and criticism.
/// Deterministic application policy remains authoritative when model text is wrong.
/// </summary>
internal static class AgentPromptPolicy
{
    public const string AuthorityAndEvidence =
        """

        ЕДИНАЯ ПОЛИТИКА ФАКТОВ И РЕШЕНИЙ:
        1. Запрос пользователя и Task Brief задают цель, scope и ограничения, но не
           доказывают содержание кадров, звук, transcript, IDs или таймкоды.
        2. Read-only sensors возвращают только измеренные факты. query — лишь provenance
           причины вызова; он не показывается нейтральным vision/audio/transcript sensors
           и сам не является evidence.
        3. Тишина, чёрный кадр, scene cut, OCR и плотность текста — кандидаты для
           исследования, а не готовая классификация и не разрешение на монтаж.
        4. Planner может только ПРЕДЛОЖИТЬ смысловую классификацию, сопоставив факты
           нескольких каналов с Task Brief; независимый critic проверяет её заново.
           Evidence действует только в реально измеренном диапазоне; отрицательное
           наблюдение нельзя распространять на остальной материал.
        5. inspect_content_overview с coverage_complete=true уже является полным coarse
           покрытием кадров. Не повторяй те же окна. Сначала исследуй самые сильные ещё
           непроверенные фактические регионы, затем уточняй границы через inspect_boundary.
        6. eligible_content_boundaries — только структурно допустимые координаты успешных
           узких probes. Наличие координаты в списке необходимо, но НЕ доказывает, что это
           нужный смысловой блок: обязательно прочитай factual_summary и channel evidence.
        7. Координаты 0 и source_duration_seconds допустимы как внешние края без boundary
           probe, но их смысл всё равно должен быть доказан. Близость к краю файла не
           доказывает опенинг, эндинг, рекламу или любой другой блок.
        8. Строго разделяй fact → hypothesis → edit decision. Не превращай OCR-строку,
           повторяющийся кадр или изменение громкости в решение. Если перекрывающиеся
           измерения противоречат друг другу, не публикуй диапазон: запроси повторное
           независимое измерение точного конфликтного окна.
        9. Для tools по task source sequence всегда используй target_kind=sequence и
           target_id=task.source_sequence_id. Никогда не называй sequence ID media ID;
           target_kind=media допустим только для реально измеренного media source ID.
        """;

    public const string PublishedPlan =
        """

        ЕДИНАЯ ПОЛИТИКА ПУБЛИКУЕМОГО ПЛАНА:
        1. plan_steps содержит только будущие editing-действия. Исследование уже завершено,
           создание Agent Draft и verification запускаются приложением автоматически;
           не добавляй для них шаги и не ставь read-only tools в план.
        2. expected_editing_tool выбирается только из editing_tools/available_tools.
           Editing tool не обязан и не должен находиться в evidence_ledger: evidence
           доказывает выбор содержимого и координат, а каталог tools — поддержку команды.
        3. Каждый editing-шаг обязан иметь полные аргументы по input_schema, достаточный
           evidence_requirement и ссылки только на успешные typed observations.
           Если на текущем source уже доступны frames+audio+transcript, content_discovery
           не может понизить требование до одного удобного канала: используй all.
        4. Для content_discovery каждая внутренняя координата ripple-delete точно копируется
           из eligible_content_boundaries и соответствующий observation_sequence включается
           в evidence_observation_sequences. Structural eligibility не заменяет смысловое
           доказательство из factual_summary, frames/audio/transcript. Выбранные каналы
           evidence_requirement должны покрывать весь удаляемый диапазон, а не только
           пересекать его небольшим измеренным фрагментом.
        5. Несколько диапазонов на одной source revision удаляются одним атомарным шагом
           ripple_delete_ranges. Несколько последовательных ripple-шагов запрещены, потому
           что первый сдвигает координаты второго.
        6. prior_plan_feedback обязателен к исправлению. Не повторяй отклонённые диапазоны
           и не превращай замечания критика в новые research/verification plan_steps.
        7. Для каждого удаляемого диапазона проверяй не только подтверждающие факты внутри,
           но и соседний защищаемый контекст по обе стороны. Наличие сюжетного материала
           внутри диапазона или неразрешённого противоречия запрещает публикацию.
        8. evidence_observation_sequences обязательно включает подробное успешное
           наблюдение, capabilities которого удовлетворяют evidence_requirement; одних
           boundary observations недостаточно. Приложение дополнительно прикрепит
           пересекающиеся факты детерминированно, но не изменит выбранные координаты.
        """;

    public const string VerificationAuthority =
        """

        ЕДИНАЯ ПОЛИТИКА ПРОВЕРКИ:
        Детерминированное приложение, а не модель, владеет pass/fail: оно проверяет source,
        draft identity, checkpoint, receipts, однократность действий, integrity, diff и
        обязательные post-edit probes. Модель может только сформулировать итоговый текст,
        не менять is_valid, не выполнять скрытые исправления и не требовать отдельный
        verification-шаг в утверждаемом плане.
        """;
}
