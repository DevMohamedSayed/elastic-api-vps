import { useState } from "react"

const API = "/api"

function Login({ onLogin }) {
  const [username, setUsername] = useState("")
  const [password, setPassword] = useState("")
  const [message, setMessage] = useState("")

  const handleLogin = async (e) => {
    e.preventDefault()
    const turnstileInput = document.querySelector("[name=cf-turnstile-response]")
    const turnstileToken = turnstileInput ? turnstileInput.value : ""

    const res = await fetch(`${API}/auth/login`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ username, password, turnstileToken }),
    })

    if (res.ok) {
      const data = await res.json()
      localStorage.setItem("jwt", data.token)
      onLogin(data.token)
      setMessage("Login successful!")
    } else {
      setMessage("Login failed - check credentials")
      if (window.turnstile) window.turnstile.reset()
    }
  }

  return (
    <section className="card">
      <h2>Login <span className="badge">JWT + Turnstile</span></h2>
      <form onSubmit={handleLogin}>
        <div className="form-row">
          <input placeholder="Username" value={username} onChange={(e) => setUsername(e.target.value)} />
          <input type="password" placeholder="Password" value={password} onChange={(e) => setPassword(e.target.value)} />
          <button type="submit">Login</button>
        </div>
        <div className="cf-turnstile" data-sitekey="0x4AAAAAACcYJQR7W2SETLP1" data-theme="dark" data-size="normal" style={{ marginTop: "1rem" }}></div>
        {message && <p style={{ marginTop: "0.5rem", color: message.includes("successful") ? "#4ade80" : "#f87171" }}>{message}</p>}
      </form>
    </section>
  )
}

export default Login
