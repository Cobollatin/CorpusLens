import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';

interface Verse { id: string; number: number; text: string; isPrimaryHighlight: boolean; jwLink?: string; wolLink?: string }
interface Result { reference: string; section?: string; verses: Verse[] }
interface Response { results: Result[] }

const copy = {
  E: { placeholder: 'Search by meaning, words, or verse (e.g. John 3:16)...', search: 'Search', searching: 'Searching...', results: 'results', oneResult: 'result', empty: 'No matching passages found. Try a different phrase or reference.', error: 'Search is unavailable. Check the API connection.', label: 'Search the Bible', theme: 'Toggle dark or light theme', examples: ['how to deal with anxiety about money', 'why does God allow suffering', 'parable of the prodigal son', 'John 3:16', 'Romans 8:31-39'] },
  S: { placeholder: 'Buscar por significado, palabras o versículo (ej. Juan 3:16)...', search: 'Buscar', searching: 'Buscando...', results: 'resultados', oneResult: 'resultado', empty: 'No se encontraron pasajes. Prueba otra frase o referencia.', error: 'La búsqueda no está disponible. Comprueba la conexión con la API.', label: 'Buscar en la Biblia', theme: 'Cambiar entre tema claro y oscuro', examples: ['cómo lidiar con la ansiedad por el dinero', 'por qué permite Dios el sufrimiento', 'parábola del hijo pródigo', 'Juan 3:16', 'Romanos 8:31-39'] }
};

@Component({ selector: 'app-root', imports: [CommonModule, FormsModule], templateUrl: './app.html', styleUrl: './app.css' })
export class App {
  private readonly http = inject(HttpClient);
  readonly api = (globalThis as any).CORPUSLENS_API_BASE_URL || '/api/v1';
  readonly language: 'E' | 'S' = location.pathname.startsWith('/es') ? 'S' : 'E';
  readonly t = copy[this.language];
  query = '';
  response?: Response;
  loading = false;
  error = '';
  dark = document.documentElement.dataset['theme'] === 'dark';

  toggleTheme() {
    this.dark = !this.dark;
    document.documentElement.dataset['theme'] = this.dark ? 'dark' : 'light';
    localStorage.setItem('corpuslens-theme', this.dark ? 'dark' : 'light');
  }

  searchExample(example: string) {
    this.query = example;
    this.search();
  }

  search() {
    if (!this.query.trim() || this.loading) return;
    this.loading = true;
    this.error = '';
    this.http.post<Response>(`${this.api}/search`, { query: this.query, language: this.language, edition: null, limit: 10, mode: 'Hybrid' }).subscribe({
      next: response => { this.response = response; this.loading = false; },
      error: error => { this.error = error?.error?.error || this.t.error; this.loading = false; }
    });
  }
}
