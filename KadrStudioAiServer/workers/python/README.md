# Production worker runtime

Один versioned Python runtime обслуживает семь изолированных процессов: `video-understanding`,
`audio-events`, `asr-align`, `diarization`, `embedding`, `director`, `critic`. Каждый процесс
получает только content-addressed IDs, сам восстанавливает путь внутри `${data-root}/assets`
и слушает gRPC только на `127.0.0.1`.

`server.py` не является mock: video worker измеряет кадры и frame-accurate shot changes,
audio worker декодирует FLAC-прокси, ASR использует локальный WhisperX/faster-whisper,
embedding — локальный sentence-transformers, director/critic — предквантизованный Q4_K_M GGUF
через отдельные project-local llama.cpp процессы и общий read-only tokenizer snapshot.
Если требуемый CUDA model отсутствует, worker возвращает типизированный `capability_unavailable`,
а не выдуманные факты.

Установка выполняется `scripts/setup-ai-workers.ps1`; runtime, модели и артефакты создаются
вне Git. Файл `requirements.lock` фиксирует Python-уровень. CUDA wheel index и model hashes
задаются setup manifest, поскольку они зависят от версии драйвера конкретной машины.
