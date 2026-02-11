import { useState, useEffect } from 'react'
import { useParams, Link } from 'react-router-dom'

const API = '/api'

function ProjectPage() {
  // useParams() reads the :slug from the URL
  // e.g., /projects/my-cool-project → slug = "my-cool-project"
  const { slug } = useParams()

  const [project, setProject] = useState(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')

  useEffect(() => {
    // Fetch project by slug (public endpoint - no JWT needed)
    fetch(`${API}/projects/by-slug/${slug}`)
      .then(res => {
        if (!res.ok) throw new Error('Project not found')
        return res.json()
      })
      .then(data => {
        setProject(data)
        setLoading(false)

        // Fetch JSON-LD and inject into page <head>
        // This is what Google reads to understand the page
        fetch(`${API}/projects/${slug}/meta`)
          .then(res => res.json())
          .then(jsonLd => {
            const script = document.createElement('script')
            script.type = 'application/ld+json'
            script.text = JSON.stringify(jsonLd)
            document.head.appendChild(script)

            // Update page title for SEO
            document.title = `${data.name} | Mohamed Sayed`
          })
      })
      .catch(err => {
        setError(err.message)
        setLoading(false)
      })
  }, [slug])  // Re-run if slug changes

  if (loading) return <div className="app"><p>Loading...</p></div>
  if (error) return (
    <div className="app">
      <h2>404 - {error}</h2>
      <Link to="/">← Back to Home</Link>
    </div>
  )

  return (
    <div className="app">
      <header>
        <Link to="/" className="back-link">← Back to Home</Link>
        <h1>{project.name}</h1>
        <p className="subtitle">
          <span className={`status ${project.status.toLowerCase()}`}>{project.status}</span>
        </p>
      </header>

      <section className="card project-detail">
        <h2>About This Project</h2>
        <p className="description">{project.description || 'No description provided.'}</p>

        <div className="meta-grid">
          <div className="meta-item">
            <strong>Created</strong>
            <span>{new Date(project.createdAt).toLocaleDateString()}</span>
          </div>
          <div className="meta-item">
            <strong>Status</strong>
            <span>{project.status}</span>
          </div>
          <div className="meta-item">
            <strong>Slug</strong>
            <span>{project.slug}</span>
          </div>
        </div>
      </section>

      <footer>
        SEO-friendly URL: mohamedsayed.site/projects/{project.slug}
      </footer>
    </div>
  )
}

export default ProjectPage
