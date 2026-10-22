FROM python:3.12-slim
RUN pip install --no-cache-dir "piper-tts[http]==1.8.0"
WORKDIR /voices
# Bake the voice into the image so the container starts offline.
RUN python -m piper.download_voices en_US-lessac-medium --data-dir /voices
EXPOSE 5000
CMD ["python", "-m", "piper.http_server", "--host", "0.0.0.0", "--port", "5000", "--data-dir", "/voices", "-m", "en_US-lessac-medium"]
