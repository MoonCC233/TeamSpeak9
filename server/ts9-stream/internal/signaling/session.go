// Package signaling 实现 TSSP v1 的 WebSocket 信令端点、会话状态机与房间模型。
//
// 规范见 docs/protocol/tssp-v1.md。
package signaling

import (
	"context"
	"encoding/json"
	"errors"
	"log/slog"
	"sync"
	"sync/atomic"
	"time"

	"github.com/coder/websocket"

	"github.com/MoonCC233/TeamSpeak9/server/ts9-stream/internal/auth"
	"github.com/MoonCC233/TeamSpeak9/server/ts9-stream/internal/tssp"
)

// sessionState 是连接级状态机的状态，见规范 §8.1。
type sessionState int

const (
	stateOpened sessionState = iota
	stateAuthenticated
	stateClosing
)

func (s sessionState) String() string {
	switch s {
	case stateOpened:
		return "OPENED"
	case stateAuthenticated:
		return "AUTHENTICATED"
	default:
		return "CLOSING"
	}
}

// Session 表示一条已建立的 TSSP 连接。
type Session struct {
	hub        *Hub
	id         string
	conn       *websocket.Conn
	logger     atomic.Pointer[slog.Logger]
	remoteAddr string

	out       chan []byte
	done      chan struct{}
	closeOnce sync.Once
	closeCode websocket.StatusCode
	closeText string

	mu           sync.Mutex
	state        sessionState
	ident        *auth.Identity
	room         roomKey
	tokenExp     time.Time
	publishing   string
	subs         map[string]struct{}
	expiryWarned bool
}

const outBufferSize = 64

func newSession(hub *Hub, id string, conn *websocket.Conn, remoteAddr string) *Session {
	s := &Session{
		hub:        hub,
		id:         id,
		conn:       conn,
		remoteAddr: remoteAddr,
		out:        make(chan []byte, outBufferSize),
		done:       make(chan struct{}),
		state:      stateOpened,
		subs:       make(map[string]struct{}),
		closeCode:  websocket.StatusNormalClosure,
	}
	s.logger.Store(hub.log.With("session", id, "peer", remoteAddr))
	return s
}

// log 返回当前日志器。鉴权后会被替换为带身份字段的版本，
// 因此用原子指针保存：readLoop 写、writeLoop 与媒体回调读。
func (s *Session) log() *slog.Logger { return s.logger.Load() }

// enrichLog 用附加字段替换日志器。
func (s *Session) enrichLog(args ...any) {
	s.logger.Store(s.logger.Load().With(args...))
}

// ID 返回会话标识。
func (s *Session) ID() string { return s.id }

// Identity 返回已鉴权的身份，未鉴权时为 nil。
func (s *Session) Identity() *auth.Identity {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.ident
}

// CLID 返回客户端在 tsserver 上的 clid，未鉴权时为 0。
func (s *Session) CLID() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.ident == nil {
		return 0
	}
	return s.ident.CLID
}

// Room 返回所在房间键。
func (s *Session) Room() roomKey {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.room
}

// run 驱动一条连接的完整生命周期。
func (s *Session) run(ctx context.Context) {
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()

	s.conn.SetReadLimit(s.hub.cfg.Limits.MaxMessageBytes)

	var wg sync.WaitGroup
	wg.Add(1)
	go func() {
		defer wg.Done()
		s.writeLoop(ctx)
	}()

	// hello 超时：规范 §8.1 要求超时后服务端主动关闭。
	helloTimer := time.AfterFunc(s.hub.cfg.Limits.HelloTimeout, func() {
		if s.State() == stateOpened {
			s.log().Debug("hello 超时，关闭连接")
			s.closeWith(websocket.StatusPolicyViolation, "hello timeout")
		}
	})
	defer helloTimer.Stop()

	s.readLoop(ctx, helloTimer)

	cancel()
	wg.Wait()
	s.hub.removeSession(s)
	s.finalClose()
}

// State 返回当前状态。
func (s *Session) State() sessionState {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.state
}

func (s *Session) readLoop(ctx context.Context, helloTimer *time.Timer) {
	for {
		readCtx, cancel := context.WithTimeout(ctx, s.hub.cfg.Runtime.ReadTimeout)
		typ, data, err := s.conn.Read(readCtx)
		cancel()
		if err != nil {
			if ctx.Err() == nil && !isNormalClose(err) {
				s.log().Debug("读取消息失败，连接结束", "err", err)
			}
			return
		}
		if typ != websocket.MessageText {
			s.replyError("", tssp.NewError(tssp.ErrBadRequest, "仅支持文本 JSON 帧"))
			continue
		}

		var env tssp.Envelope
		if err := json.Unmarshal(data, &env); err != nil {
			s.replyError("", tssp.NewError(tssp.ErrBadRequest, "JSON 解析失败"))
			continue
		}
		if env.Type == "" {
			s.replyError(env.ID, tssp.NewError(tssp.ErrBadRequest, "缺少消息类型 t"))
			continue
		}

		if env.Type == tssp.TypeHello {
			helloTimer.Stop()
		}
		s.dispatch(ctx, &env)
	}
}

func (s *Session) writeLoop(ctx context.Context) {
	ping := time.NewTicker(s.hub.cfg.Runtime.PingInterval)
	defer ping.Stop()

	for {
		select {
		case <-ctx.Done():
			return
		case <-s.done:
			s.drainAndClose()
			return
		case msg := <-s.out:
			writeCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
			err := s.conn.Write(writeCtx, websocket.MessageText, msg)
			cancel()
			if err != nil {
				s.log().Debug("发送消息失败", "err", err)
				return
			}
		case <-ping.C:
			pingCtx, cancel := context.WithTimeout(ctx, s.hub.cfg.Runtime.PingInterval)
			err := s.conn.Ping(pingCtx)
			cancel()
			if err != nil {
				s.log().Debug("心跳失败，连接结束", "err", err)
				return
			}
		}
	}
}

// drainAndClose 在收到关闭信号后尽力把缓冲区里的消息发完（例如 bye），再关闭连接。
func (s *Session) drainAndClose() {
	deadline := time.Now().Add(2 * time.Second)
	for {
		select {
		case msg := <-s.out:
			ctx, cancel := context.WithDeadline(context.Background(), deadline)
			err := s.conn.Write(ctx, websocket.MessageText, msg)
			cancel()
			if err != nil {
				return
			}
			if time.Now().After(deadline) {
				return
			}
		default:
			return
		}
	}
}

// enqueue 把已编码的消息放入发送队列。队列满说明客户端消费不过来，直接断开。
func (s *Session) enqueue(msg []byte) {
	select {
	case <-s.done:
		return
	default:
	}
	select {
	case s.out <- msg:
	default:
		s.log().Warn("发送队列已满，断开连接")
		s.closeWith(websocket.StatusTryAgainLater, "send buffer overflow")
	}
}

// send 编码并发送一条消息。
func (s *Session) send(msgType, id string, payload any) {
	data, err := encode(msgType, id, payload)
	if err != nil {
		s.log().Error("编码消息失败", "type", msgType, "err", err)
		return
	}
	s.enqueue(data)
}

// replyOK 回复成功响应。
func (s *Session) replyOK(id string, payload any) {
	if id == "" {
		return
	}
	s.send(tssp.TypeOK, id, payload)
}

// replyError 回复错误响应。没有请求 id 时也会发出，便于客户端记录日志。
func (s *Session) replyError(id string, err *tssp.Error) {
	s.send(tssp.TypeError, id, err)
}

// closeWith 请求关闭连接。
func (s *Session) closeWith(code websocket.StatusCode, reason string) {
	s.closeOnce.Do(func() {
		s.mu.Lock()
		s.state = stateClosing
		s.closeCode = code
		s.closeText = reason
		s.mu.Unlock()
		close(s.done)
	})
}

// Bye 发送 bye 事件并关闭连接。
func (s *Session) Bye(code, message string) {
	s.send(tssp.EventBye, "", tssp.ByeEvent{Code: code, Message: message})
	s.closeWith(websocket.StatusNormalClosure, code)
}

func (s *Session) finalClose() {
	s.mu.Lock()
	code, reason := s.closeCode, s.closeText
	s.mu.Unlock()
	if code == 0 {
		code = websocket.StatusNormalClosure
	}
	if err := s.conn.Close(code, truncateReason(reason)); err != nil {
		_ = s.conn.CloseNow()
	}
}

// authorize 校验请求携带的令牌，返回身份。
func (s *Session) authorize(token string) (*auth.Identity, *tssp.Error) {
	s.mu.Lock()
	state := s.state
	ident := s.ident
	s.mu.Unlock()

	if state != stateAuthenticated || ident == nil {
		return nil, tssp.NewError(tssp.ErrTokenInvalid, "尚未完成 hello 鉴权")
	}
	if token == "" {
		return nil, tssp.NewError(tssp.ErrTokenInvalid, "缺少 token")
	}
	claims, err := s.hub.signer.Verify(token, time.Now())
	if err != nil {
		return nil, tssp.AsError(err)
	}
	if claims.SessionID != s.id {
		return nil, tssp.NewError(tssp.ErrTokenInvalid, "令牌与当前连接不匹配")
	}
	return ident, nil
}

// setAuthenticated 在 hello 成功后写入身份。
func (s *Session) setAuthenticated(ident *auth.Identity, exp time.Time) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.state = stateAuthenticated
	s.ident = ident
	s.room = roomKey{server: ident.ServerHash, cid: ident.CID}
	s.tokenExp = exp
	s.expiryWarned = false
}

// updateIdentity 在 renew 后刷新身份与令牌有效期。
func (s *Session) updateIdentity(ident *auth.Identity, exp time.Time) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.ident = ident
	s.room = roomKey{server: ident.ServerHash, cid: ident.CID}
	s.tokenExp = exp
	s.expiryWarned = false
}

func (s *Session) setPublishing(streamID string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.publishing = streamID
}

func (s *Session) publishingStream() string {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.publishing
}

func (s *Session) addSubscription(streamID string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.subs[streamID] = struct{}{}
}

func (s *Session) removeSubscription(streamID string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	delete(s.subs, streamID)
}

func (s *Session) subscriptionList() []string {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]string, 0, len(s.subs))
	for id := range s.subs {
		out = append(out, id)
	}
	return out
}

// maybeWarnExpiry 在令牌接近过期时发出一次 token_expiring。
func (s *Session) maybeWarnExpiry(now time.Time, leeway time.Duration) {
	s.mu.Lock()
	if s.state != stateAuthenticated || s.expiryWarned || s.tokenExp.IsZero() {
		s.mu.Unlock()
		return
	}
	if now.Add(leeway).Before(s.tokenExp) {
		s.mu.Unlock()
		return
	}
	s.expiryWarned = true
	exp := s.tokenExp
	s.mu.Unlock()

	s.send(tssp.EventTokenExpiring, "", tssp.TokenExpiringEvent{ExpiresAt: exp.UnixMilli()})
}

// tokenDeadPassed 判断令牌是否已过期。
func (s *Session) tokenDeadPassed(now time.Time) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.state == stateAuthenticated && !s.tokenExp.IsZero() && now.After(s.tokenExp)
}

func encode(msgType, id string, payload any) ([]byte, error) {
	env := tssp.Envelope{
		Type: msgType,
		ID:   id,
		TS:   time.Now().UnixMilli(),
	}
	if payload != nil {
		raw, err := json.Marshal(payload)
		if err != nil {
			return nil, err
		}
		env.Data = raw
	}
	return json.Marshal(&env)
}

func isNormalClose(err error) bool {
	status := websocket.CloseStatus(err)
	if status == websocket.StatusNormalClosure || status == websocket.StatusGoingAway {
		return true
	}
	return errors.Is(err, context.Canceled)
}

func truncateReason(reason string) string {
	// WebSocket 关闭原因上限 123 字节。
	const max = 120
	if len(reason) <= max {
		return reason
	}
	return reason[:max]
}
