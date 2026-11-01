FROM python:3.13-slim
ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /app
COPY tools/session-importer/importer.py /app/importer.py
USER 65532:65532
ENTRYPOINT ["python", "-I", "-u", "/app/importer.py"]
