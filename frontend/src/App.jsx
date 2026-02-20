import { useState } from "react"
import { BrowserRouter, Routes, Route, Link } from "react-router-dom"
import ProjectPage from "./ProjectPage"
import Login from "./Login"
import "./App.css"

const API = "/api"

function App() {
  const [projects, setProjects] = useState([])
  const [files, setFiles] = useState([])
  const [searchResults, setSearchResults] = useState([])
  const [projName, setProjName] = useState("")
  const [projDesc, setProjDesc] = useState("")
  const [projStatus, setProjStatus] = useState("Active")
  const [searchCity, setSearchCity] = useState("")
  const [message, setMessage] = useState("")
  const [token, setToken] = useState(localStorage.getItem("jwt") || "")

  const showMsg = (msg) => { setMessage(msg); setTimeout(() => setMessage(""), 3000) }

  const logout = () => {
    setToken("")
    localStorage.removeItem("jwt")
    showMsg("Logged out")
  }

  const loadProjects = async () => {
    const res = await fetch(`${API}/projects`)
    setProjects(await res.json())
  }

  const createProject = async () => {
    if (!projName) return showMsg("Enter a project name")
    const res = await fetch(`${API}/projects`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
      body: JSON.stringify({ name: projName, description: projDesc, status: projStatus })
    })
    if (res.status === 401) return showMsg("Login first!")
    setProjName(""); setProjDesc("")
    showMsg("Project created!")
    loadProjects()
  }

  const deleteProject = async (id) => {
    await fetch(`${API}/projects/${id}`, { method: "DELETE", headers: { Authorization: `Bearer ${token}` } })
    showMsg("Project deleted")
    loadProjects()
  }

  const loadFiles = async () => {
    const res = await fetch(`${API}/files`, { headers: { Authorization: `Bearer ${token}` } })
    setFiles(await res.json())
  }

  const uploadFile = async (e) => {
    const file = e.target.files[0]
    if (!file) return
    const form = new FormData()
    form.append("file", file)
    await fetch(`${API}/files/upload`, { method: "POST", headers: { Authorization: `Bearer ${token}` }, body: form })
    showMsg(`Uploaded: ${file.name}`)
    loadFiles()
  }

  const deleteFile = async (key) => {
    await fetch(`${API}/files/${key}`, { method: "DELETE", headers: { Authorization: `Bearer ${token}` } })
    showMsg("File deleted")
    loadFiles()
  }

  const searchUsers = async () => {
    if (!searchCity) return
    const res = await fetch(`${API}/users/search/${searchCity}`)
    setSearchResults(await res.json())
  }

  return (
    <div className="app">
      <header>
        <h1>Mohamed Sayed</h1>
        <p className="subtitle">Senior .NET Developer | VPS Infrastructure Project</p>
        {message && <div className="toast">{message}</div>}
      </header>

      <div className="stack-grid">
        {["Nginx|Reverse Proxy", "Elasticsearch|Search Engine", "SQL Server|Relational DB",
          "MinIO|Object Storage", "Prometheus|Metrics", "Grafana|Dashboards",
          "Cloudflare|CDN + WAF", ".NET 8|API", "Docker|Containers"].map(item => {
          const [name, desc] = item.split("|")
          return <div key={name} className="stack-card"><strong>{name}</strong><span>{desc}</span></div>
        })}
      </div>

      {!token ? (
        <Login onLogin={(t) => setToken(t)} />
      ) : (
        <>
          <section className="card">
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
              <h2>Welcome, Admin</h2>
              <button className="danger small" onClick={logout}>Logout</button>
            </div>
          </section>

          <section className="card">
            <h2>Projects <span className="badge">SQL Server + EF Core</span></h2>
            <div className="form-row">
              <input placeholder="Project name" value={projName} onChange={e => setProjName(e.target.value)} />
              <input placeholder="Description" value={projDesc} onChange={e => setProjDesc(e.target.value)} />
              <select value={projStatus} onChange={e => setProjStatus(e.target.value)}>
                <option>Active</option>
                <option>Completed</option>
                <option>Archived</option>
              </select>
              <button onClick={createProject}>Create</button>
            </div>
            <button className="secondary" onClick={loadProjects}>Load Projects</button>
            {projects.length > 0 && (
              <table>
                <thead><tr><th>Name</th><th>Description</th><th>Status</th><th>Created</th><th></th></tr></thead>
                <tbody>
                  {projects.map(p => (
                    <tr key={p.id}>
                      <td><Link to={`/projects/${p.slug}`}>{p.name}</Link></td>
                      <td>{p.description}</td>
                      <td><span className={`status ${p.status.toLowerCase()}`}>{p.status}</span></td>
                      <td>{new Date(p.createdAt).toLocaleDateString()}</td>
                      <td><button className="danger small" onClick={() => deleteProject(p.id)}>Delete</button></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </section>

          <section className="card">
            <h2>File Storage <span className="badge">MinIO S3</span></h2>
            <div className="form-row">
              <input type="file" onChange={uploadFile} />
            </div>
            <button className="secondary" onClick={loadFiles}>Load Files</button>
            {files.length > 0 && (
              <table>
                <thead><tr><th>Filename</th><th>Size</th><th>Modified</th><th></th></tr></thead>
                <tbody>
                  {files.map(f => (
                    <tr key={f.key}>
                      <td>{f.key.substring(37)}</td>
                      <td>{(f.size / 1024).toFixed(1)} KB</td>
                      <td>{new Date(f.lastModified).toLocaleDateString()}</td>
                      <td><button className="danger small" onClick={() => deleteFile(f.key)}>Delete</button></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </section>

          <section className="card">
            <h2>Search <span className="badge">Elasticsearch</span></h2>
            <div className="form-row">
              <input placeholder="Search users by city..." value={searchCity} onChange={e => setSearchCity(e.target.value)}
                onKeyDown={e => e.key === "Enter" && searchUsers()} />
              <button onClick={searchUsers}>Search</button>
            </div>
            {searchResults.length > 0 && (
              <pre className="results">{JSON.stringify(searchResults, null, 2)}</pre>
            )}
          </section>
        </>
      )}

      <footer>
        11 Docker Containers | Cloudflare CDN + Turnstile | Auto-deployed via GitHub Webhooks
      </footer>
    </div>
  )
}

function Root() {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/" element={<App />} />
        <Route path="/projects/:slug" element={<ProjectPage />} />
      </Routes>
    </BrowserRouter>
  )
}

export default Root
