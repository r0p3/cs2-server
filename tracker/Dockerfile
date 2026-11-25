FROM python:3.13-slim
WORKDIR /app
COPY tracker/server.py tracker/index.html tracker/app.js tracker/style.css ./
USER 1000:1000
EXPOSE 8080
CMD ["python", "-u", "server.py"]
