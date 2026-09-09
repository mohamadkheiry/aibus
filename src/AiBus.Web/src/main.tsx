import React from 'react'
import ReactDOM from 'react-dom/client'
import { Toaster } from 'react-hot-toast'
import '@fontsource-variable/vazirmatn'
import './styles.css'
import App from './App'

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode><App /><Toaster position="top-center" toastOptions={{className:'toast'}} /></React.StrictMode>
)
