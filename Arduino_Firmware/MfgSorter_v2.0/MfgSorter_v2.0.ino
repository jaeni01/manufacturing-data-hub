/**
 * MfgSorter_v2.0_Integrated
 *
 * C# PC (MfgInspectionSystem v8) 연동 통합 펌웨어
 *
 * 동작 흐름:
 *   1. S1 감지 → PC에 IR 이벤트 전송
 *   2. 모터 STOP_TIME(5초) 정지  →  PC가 카메라 검사 수행
 *   3. PC가 판정 전송 (DEFECT→servo A, HOLD→servo B, PASS→없음)
 *   4. TOTAL_DELAY 후 서보 발동 (판정이 DEFECT/HOLD인 경우만)
 *   5. SERVO_HOLD_MS 후 서보 복귀
 *   6. 모터 재시작
 *
 * 라이브러리: Servo, ArduinoJson, avr/wdt (내장)
 * 펌웨어 버전: 2.0.1_DualServo
 */

#include <Servo.h>
#include <ArduinoJson.h>
#include <avr/wdt.h>

// ═══════════════════════════════════════════════
//  핀 정의
// ═══════════════════════════════════════════════
#define SERVO_PIN_A   44   // 서보모터 A (DEFECT)
#define SERVO_PIN_B   47   // 서보모터 B (HOLD)
#define PIN_S1        18   // S1: 제품 감지 센서 (인터럽트 가능)
#define PIN_S2        19   // S2: 예비
#define PIN_S3        20   // S3: 예비
#define BUTTON_PIN     2   // E-STOP 버튼 (인터럽트 가능)
#define BUZZER_PIN    11   // 버저
#define MOTOR_B1A      7   // 모터 드라이버 PWM
#define MOTOR_B1B      4   // 모터 드라이버 방향

// ═══════════════════════════════════════════════
//  서보 각도 — A/B 독립 설정
// ═══════════════════════════════════════════════
#define START_A     0    // 서보 A 대기 각도 (0→180 방향으로 쳐냄)
#define ACTION_A  180    // 서보 A 타격 각도

#define START_B   180    // 서보 B 대기 각도 (180→0 방향으로 쳐냄)
#define ACTION_B   0    // 서보 B 타격 각도

// ═══════════════════════════════════════════════
//  타이밍 설정 (단위: ms)
// ═══════════════════════════════════════════════
const unsigned long STOP_TIME      =  5000; // S1 감지 후 모터 정지 시간
const unsigned long TOTAL_DELAY_A  =  8000; // S1 감지 후 서보 A 발동 시간 (DEFECT)
const unsigned long TOTAL_DELAY_B  =  8000; // S1 감지 후 서보 B 발동 시간 (HOLD)
const unsigned long SERVO_HOLD_A_MS =   500; // 서보 A 발동 후 복귀 대기 시간
const unsigned long SERVO_HOLD_B_MS =   500; // 서보 B 발동 후 복귀 대기 시간 (빠른 복귀로 쳐냄)
const unsigned long CYCLE_TIMEOUT  = 11000; // 사이클 최대 허용 시간 (안전 종료)
const unsigned long HB_INTERVAL_MS =  1000; // 하트비트 전송 주기
const unsigned long SENSOR_INTERVAL=   500; // 센서 데이터 전송 주기
const int           MOTOR_SPEED    =   85; // 모터 재시작 속도 (0~255)
// 서보 복귀 후 컨베이어 진동으로 인한 S1 오감지 억제 (사이클 종료 시점부터 기산)
const unsigned long S1_LOCKOUT_MS  =  3000;

const char* FW_VERSION = "2.0.1_DualServo";

// ═══════════════════════════════════════════════
//  전역 변수
// ═══════════════════════════════════════════════
Servo servoA;
Servo servoB;

// 처리 사이클 상태
bool          isProcessing    = false;
unsigned long startTime       = 0;
char          pendingTarget   = '\0'; // 'A', 'B', 또는 없음
bool          servoActive     = false;
unsigned long servoFireTime   = 0;
unsigned long lastCycleEndMs  = 0;   // 사이클 종료 시각 (S1 lockout 기산점)

// 시스템 상태
volatile bool motorStopped = false;
int           raspiStatus  = 1;

unsigned long lastHbMs      = 0;
unsigned long lastSensorMs  = 0;
unsigned long seqCounter    = 0;
bool          lastS1        = false;
bool          lastS2        = false;
bool          lastS3        = false;
unsigned long lastS2Ms      = 0;   // S2 디바운스
unsigned long lastS3Ms      = 0;   // S3 디바운스

// ═══════════════════════════════════════════════
//  직렬 전송 헬퍼
// ═══════════════════════════════════════════════
void sendAck(const char* cmd, long seq, const char* result) {
  StaticJsonDocument<128> doc;
  doc["evt"]    = "ack";
  doc["cmd"]    = cmd;
  doc["seq"]    = seq;
  doc["result"] = result;
  serializeJson(doc, Serial);
  Serial.println();
}

void sendIrEvent(const char* sensor, const char* state) {
  StaticJsonDocument<128> doc;
  doc["evt"]    = "ir";
  doc["sensor"] = sensor;
  doc["state"]  = state;
  doc["ts"]     = millis();
  doc["seq"]    = ++seqCounter;
  serializeJson(doc, Serial);
  Serial.println();
}

// ═══════════════════════════════════════════════
//  인터럽트: E-STOP 버튼
// ═══════════════════════════════════════════════
void isrEStop() {
  motorStopped = (digitalRead(BUTTON_PIN) == HIGH);
}

// ═══════════════════════════════════════════════
//  setup
// ═══════════════════════════════════════════════
void setup() {
  Serial.begin(115200);

  servoA.attach(SERVO_PIN_A);
  servoA.write(START_A);
  servoB.attach(SERVO_PIN_B);
  servoB.write(START_B);

  pinMode(BUZZER_PIN,  OUTPUT);
  pinMode(BUTTON_PIN,  INPUT_PULLUP);
  attachInterrupt(digitalPinToInterrupt(BUTTON_PIN), isrEStop, CHANGE);

  pinMode(PIN_S1, INPUT_PULLUP);
  pinMode(PIN_S2, INPUT_PULLUP);
  pinMode(PIN_S3, INPUT_PULLUP);
  pinMode(MOTOR_B1A, OUTPUT);
  pinMode(MOTOR_B1B, OUTPUT);

  wdt_enable(WDTO_8S);

  StaticJsonDocument<96> bootDoc;
  bootDoc["evt"]        = "boot";
  bootDoc["fw_version"] = FW_VERSION;
  serializeJson(bootDoc, Serial);
  Serial.println();
}

// ═══════════════════════════════════════════════
//  loop
// ═══════════════════════════════════════════════
void loop() {
  wdt_reset();
  unsigned long now = millis();

  // ── 1. E-STOP 버저 ──────────────────────────
  motorStopped ? tone(BUZZER_PIN, 1000) : noTone(BUZZER_PIN);

  // ── 2. PC 명령 수신 ─────────────────────────
  if (Serial.available() > 0) {
    if (Serial.peek() == '{') {
      StaticJsonDocument<256> cmdDoc;
      if (!deserializeJson(cmdDoc, Serial)) {
        const char* cmd = cmdDoc["cmd"] | "";
        long        seq = cmdDoc["seq"] | 0;

        if (strcmp(cmd, "motor") == 0) {
          raspiStatus = ((int)(cmdDoc["value"] | 0) > 0) ? 1 : 0;
          sendAck(cmd, seq, "ok");
        }
        else if (strcmp(cmd, "start") == 0) {
          raspiStatus = 1;
          sendAck(cmd, seq, "ok");
        }
        else if (strcmp(cmd, "stop") == 0) {
          raspiStatus = 0;
          sendAck(cmd, seq, "ok");
        }
        else if (strcmp(cmd, "servo") == 0) {
          const char* target = cmdDoc["target"] | "";
          if (isProcessing && pendingTarget == '\0') {
            if (strcmp(target, "A") == 0) pendingTarget = 'A';
            else if (strcmp(target, "B") == 0) pendingTarget = 'B';
          }
          sendAck(cmd, seq, "ok");
        }
        else if (strcmp(cmd, "ping") == 0) {
          sendAck(cmd, seq, "ok");
        }
        else if (strcmp(cmd, "estop") == 0) {
          motorStopped = true;
          sendAck(cmd, seq, "ok");
        }
        else if (strcmp(cmd, "estop_release") == 0) {
          motorStopped = false;
          sendAck(cmd, seq, "ok");
        }
      }
    } else {
      Serial.read();
    }
  }

  // ── 3. S1 감지 (상승 에지, 사이클 중복 방지) ─
  // isProcessing : 사이클 진행 중 재진입 차단
  // S1_LOCKOUT_MS: 서보 복귀 후 벨트 진동으로 인한 오감지 억제
  bool s1 = (digitalRead(PIN_S1) == LOW);
  if (s1 && !lastS1 && !isProcessing && (now - lastCycleEndMs >= S1_LOCKOUT_MS)) {
    isProcessing  = true;
    startTime     = now;
    pendingTarget = '\0';
    servoActive   = false;
    sendIrEvent("S1", "blocked");
  }
  lastS1 = s1;

  // ── 3b. S2/S3 감지 (분류 검증용, 500ms 디바운스) ─
  bool s2 = (digitalRead(PIN_S2) == LOW);
  if (s2 && !lastS2 && (now - lastS2Ms >= 500)) {
    sendIrEvent("S2", "blocked");
    lastS2Ms = now;
  }
  lastS2 = s2;

  bool s3 = (digitalRead(PIN_S3) == LOW);
  if (s3 && !lastS3 && (now - lastS3Ms >= 500)) {
    sendIrEvent("S3", "blocked");
    lastS3Ms = now;
  }
  lastS3 = s3;

  // ── 4. E-STOP 강제 중단 ─────────────────────
  // 비상정지 상태에서 사이클이 남아 있으면 서보를 즉시 복귀시키고 사이클 종료.
  // 이 블록을 서보 발동(5) 이전에 평가하므로 발동 자체도 차단된다.
  if (motorStopped && isProcessing) {
    if (servoActive) {
      if (pendingTarget == 'A') servoA.write(START_A);
      else                      servoB.write(START_B);
      servoActive = false;
    }
    pendingTarget  = '\0';
    isProcessing   = false;
    lastCycleEndMs = now;
  }

  // ── 5. 서보 발동 ────────────────────────────
  unsigned long fireDelay = (pendingTarget == 'A') ? TOTAL_DELAY_A : TOTAL_DELAY_B;
  if (isProcessing && !servoActive && pendingTarget != '\0' &&
      !motorStopped &&                          // 비상정지 중 발동 차단
      (now - startTime >= fireDelay)) {
    if (pendingTarget == 'A') servoA.write(ACTION_A);
    else                      servoB.write(ACTION_B);
    servoActive   = true;
    servoFireTime = now;
  }

  // ── 6. 서보 복귀 ────────────────────────────
  unsigned long holdMs = (pendingTarget == 'A') ? SERVO_HOLD_A_MS : SERVO_HOLD_B_MS;
  if (servoActive && (now - servoFireTime >= holdMs)) {
    if (pendingTarget == 'A') servoA.write(START_A);
    else                      servoB.write(START_B);
    servoActive      = false;
    pendingTarget    = '\0';
    isProcessing     = false;
    lastCycleEndMs   = now;  // lockout 기산 — 벨트 진동 억제 시작
  }

  // ── 7. 사이클 종료 (CYCLE_TIMEOUT 안전 종료) ─
  if (isProcessing && (now - startTime >= CYCLE_TIMEOUT)) {
    if (servoActive) {
      if (pendingTarget == 'A') servoA.write(START_A);
      else                      servoB.write(START_B);
      servoActive = false;
    }
    pendingTarget  = '\0';
    isProcessing   = false;
    lastCycleEndMs = now;  // 타임아웃 종료도 lockout 기산
  }

  // ── 8. 모터 제어 ────────────────────────────
  bool motorRun = !motorStopped
               && (raspiStatus == 1)
               && !(isProcessing && (now - startTime < STOP_TIME));

  if (motorRun) {
    analogWrite(MOTOR_B1A, MOTOR_SPEED);
    digitalWrite(MOTOR_B1B, LOW);
  } else {
    analogWrite(MOTOR_B1A, 0);
    digitalWrite(MOTOR_B1B, LOW);
  }

  // ── 9. 하트비트 ─────────────────────────────
  if (now - lastHbMs >= HB_INTERVAL_MS) {
    StaticJsonDocument<96> hbDoc;
    hbDoc["evt"]    = "hb";
    hbDoc["uptime"] = now;
    hbDoc["seq"]    = ++seqCounter;
    serializeJson(hbDoc, Serial);
    Serial.println();
    lastHbMs = now;
  }

  // ── 10. 센서 데이터 (0.5초 주기) ────────────
  if (now - lastSensorMs >= SENSOR_INTERVAL) {
    StaticJsonDocument<128> doc;
    doc["evt"] = "sensor";
    doc["st"]  = raspiStatus;
    doc["s1"]  = s1 ? 1 : 0;
    doc["s2"]  = (digitalRead(PIN_S2) == LOW) ? 1 : 0;
    doc["s3"]  = (digitalRead(PIN_S3) == LOW) ? 1 : 0;
    doc["seq"] = ++seqCounter;
    serializeJson(doc, Serial);
    Serial.println();
    lastSensorMs = now;
  }
}
