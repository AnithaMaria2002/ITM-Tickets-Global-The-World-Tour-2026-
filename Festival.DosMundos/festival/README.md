# 🎵 Festival de los Dos Mundos - Sistema de Boletería

**Estudiantes ITM S.A.S.** | Arquitectura de Software - Nivel 5 | .NET 8.0

Ecosistema de microservicios para gestionar la venta de boletas del Festival de los Dos Mundos (Medellín 🇨🇴 y Madrid 🇪🇸), diseñado para soportar **50,000 usuarios concurrentes** en el minuto cero de venta.

---

## 🏗️ Arquitectura del Sistema

```
[App MAUI] ──HTTPS──▶ [Gateway YARP] ──▶ [Order.Api]   ──gRPC──▶ [Inventory.Api]
                            │              │  SAGA          │
                       JWT + Rate         │  ├──HTTP──▶ [Price.Api]    ──Redis──▶ 📦
                        Limiting          │  └──Pub──▶  RabbitMQ
                                          │                │
                                    SignalR◀──────── [Ticket.Api] (Consumer)
                                          │
                                     [Search.Api] ──▶ Elasticsearch + Qdrant
```

---

## 🧩 Microservicios

| Servicio | Puerto | Tecnología clave | Responsabilidad |
|---|---|---|---|
| `Festival.Gateway.Api` | 8080 | YARP + JWT + Rate Limiting | Punto único de entrada, seguridad perimetral |
| `Festival.Inventory.Api` | 5100/5101 | gRPC + .NET 8 | Dueño del stock de boletas |
| `Festival.Order.Api` | 5000 | SAGA + MassTransit | Orquestador de la compra |
| `Festival.Price.Api` | 5200 | Redis Cache | Precios dinámicos con caché distribuida |
| `Festival.Ticket.Api` | 5300 | MassTransit + SignalR | Generación de boletas + notificaciones RT |
| `Festival.Search.Api` | 5400 | Elasticsearch + Qdrant | Búsqueda por texto e IA semántica |
| `Festival.Store.Mobile` | — | .NET MAUI | App móvil Android/iOS |

---

## 🚀 Cómo correr el proyecto

### Prerrequisitos
- Docker Desktop
- .NET 8 SDK
- Android Emulator o dispositivo físico (para MAUI)

### Opción 1: Docker Compose (recomendado)
```bash
# Levanta TODO el ecosistema con un solo comando
docker-compose up --build

# El Gateway queda disponible en: http://localhost:8080
# RabbitMQ Management: http://localhost:15672 (guest/guest)
# Elasticsearch: http://localhost:9200
# Qdrant: http://localhost:6333
```

### Opción 2: Ejecución local (desarrollo)
```bash
# Terminal 1: Infraestructura
docker run -d -p 5672:5672 -p 15672:15672 rabbitmq:3.13-management
docker run -d -p 6379:6379 redis:7.2-alpine
docker run -d -p 9200:9200 -e "discovery.type=single-node" -e "xpack.security.enabled=false" docker.elastic.co/elasticsearch/elasticsearch:8.13.0
docker run -d -p 6333:6333 qdrant/qdrant:v1.9.2

# Terminales 2-7: Cada microservicio (en su carpeta respectiva)
dotnet run --project Festival.Inventory.Api
dotnet run --project Festival.Price.Api
dotnet run --project Festival.Ticket.Api
dotnet run --project Festival.Order.Api
dotnet run --project Festival.Search.Api
dotnet run --project Festival.Gateway.Api
```

### Opción 3: Kubernetes
```bash
kubectl apply -f k8s/00-namespace-config.yaml
kubectl apply -f k8s/01-infrastructure.yaml
kubectl apply -f k8s/02-microservices-hpa.yaml

# Ver el estado
kubectl get pods -n festival
kubectl get hpa -n festival
```

---

## 🔑 Cómo obtener un token JWT de prueba

Genera el token en [jwt.io](https://jwt.io) con:
- **Header:** `{"alg": "HS256", "typ": "JWT"}`
- **Payload:**
```json
{
  "iss": "FestivalIdentityServer",
  "aud": "FestivalApis",
  "email": "admin@festival.com",
  "role": "Administrador",
  "exp": 9999999999
}
```
- **Secret:** `Festival-Dos-Mundos-Super-Secret-Key-2026-ITM-Nivel5`

---

## 🔄 Flujo de Compra (SAGA Orquestada)

```
Usuario MAUI → POST /api/orders
     │
     ▼
Order.Api
  ├── PASO 1: gRPC → Inventory.Api (¿hay boletas?)
  │     └── Si NO hay → 400 Bad Request (ABORT, nada que compensar)
  │     └── Si SÍ hay → reserva boletas (ACCIÓN)
  │
  ├── PASO 2: HTTP → Price.Api (¿cuánto cuesta?) [con Redis Cache]
  │
  ├── PASO 3: Procesar Pago
  │     └── Si FALLA → COMPENSACIÓN: gRPC → Inventory.Api (devolver boletas)
  │                   + Publicar OrderCancelledEvent
  │
  └── PASO 4: Si TODO OK → Publicar OrderCreatedEvent → RabbitMQ
                    │
                    ▼
              Ticket.Api (Consumer)
                ├── Genera boleta + QR
                ├── Publica TicketGeneratedEvent
                └── SignalR → Notifica MAUI en tiempo real 🎉
```

---

## ⚡ Por qué gRPC entre Order e Inventory

En el minuto cero con 50,000 usuarios:
- **HTTP + JSON:** serialización en texto, headers HTTP, ~5ms por llamada
- **gRPC + Protobuf:** serialización binaria, HTTP/2 multiplexing, ~0.5ms por llamada
- **Resultado:** 10x más rápido en el cuello de botella más crítico del sistema

---

## 🧠 Búsqueda Inteligente

```
GET /api/search/text?q=rock            → Elasticsearch (fuzzy: "rok" → "rock")
GET /api/search/semantic?vibe=bailar   → Qdrant (semántico: intención del usuario)
```

- **Elasticsearch:** índice invertido, analiza español, tolera errores de escritura
- **Qdrant:** vectores semánticos (embeddings) generados por IA, entiende el significado

---

## 📊 Rúbrica de Calificación

| Criterio | Implementación |
|---|---|
| Integración Funcional (1.5) | MAUI → Gateway → Order → gRPC → Inventory → MassTransit → Ticket → **SignalR** → MAUI |
| Resiliencia y SAGA (1.0) | Compensación automática gRPC + RabbitMQ guarda mensajes si Ticket.Api cae |
| Rendimiento Redis/gRPC (1.0) | Redis TTL 5min (90% cache hit) + gRPC binario <1ms |
| DevOps y Cloud (1.0) | Dockerfile multi-stage + GitHub Actions + Kubernetes HPA |
| IA Semántica (0.5) | Elasticsearch fuzzy + Qdrant vector search |

---

## 📁 Estructura del Proyecto

```
Festival.Store.System.slnx
├── Festival.Gateway.Api/          # YARP + JWT + Rate Limiting
├── Festival.Inventory.Api/        # gRPC Server + REST
│   └── Protos/inventory.proto
├── Festival.Order.Api/            # SAGA Orchestrator
│   └── Protos/inventory.proto     # (gRPC Client)
├── Festival.Price.Api/            # Redis Cache
├── Festival.Ticket.Api/           # MassTransit Consumer + SignalR Hub
├── Festival.Search.Api/           # Elasticsearch + Qdrant
├── Festival.Shared.Events/        # Records de eventos (inmutables)
├── Festival.Store.Mobile/         # .NET MAUI App
├── k8s/                           # Manifiestos de Kubernetes
│   ├── 00-namespace-config.yaml
│   ├── 01-infrastructure.yaml
│   └── 02-microservices-hpa.yaml
├── .github/workflows/
│   └── ci-cd.yml                  # Pipeline de CI/CD
└── docker-compose.yml             # Orquestación local completa
```
