import { useState, useEffect, useRef } from "react"

const API = "/api"

function Login({ onLogin }) {
  const [username, setUsername] = useState("")
  const [password, setPassword] = useState("")
  const [message, setMessage] = useState("")
  const [turnstileToken, setTurnstileToken] = useState("")
  const turnstileRef = useRef(null)

  useEffect(() => {
    // Load Turnstile script
    const script = document.createElement("script")
    script.src = "https://challenges.cloudflare.com/turnstile/v0/api.js"
    script.async = true
    document.head.appendChild(script)

    script.onload = () => {
      if (window.turnstile && turnstileRef.current) {
        window.turnstile.render(turnstileRef.current, {
          sitekey: "0x4AAAAAAACb7QXWwuyS5KORH",
          callback: (token) => setTurnstileToken(token),
        })
      }
    }
  }, [])

  const handleLogin = async (e) => {
    e.preventDefault()
    if (!turnstileToken) {
      setMessage("Please complete the verification")
      return
    }

    const res = await fetch(`${API}/auth/login`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        username,
        password,
        turnstileToken,
      }),
    })

    if (res.ok) {
      const data = await res.json()
      localStorage.setItem("jwt", data.token)
      onLogin(data.token)
      setMessage("Login successful!")
    } else {
      setMessage("Login failed - check credentials")
      // Reset turnstile
      if (window.turnstile) window.turnstile.reset()
      setTurnstileToken("")
    }
  }

  return (
    <section className="card">
      <h2>Login <span className="badge">JWT + Turnstile</span></h2>
      <form onSubmit={handleLogin}>
        <div className="form-row">
          <input
            placeholder="Username"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
          />
          <input
            type="password"
            placeholder="Password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
          <button type="submit">Login</button>
        </div>
        <div ref={turnstileRef} style={{ marginTop: "1rem" }}></div>
        {message && <p style={{ marginTop: "0.5rem", color: message.includes("successful") ? "#4ade80" : "#f87171" }}>{message}</p>}
      </form>
    </section>
  )
}

export default Login
