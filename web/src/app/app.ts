import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';

interface Verse { id: string; number: number; text: string; isPrimaryHighlight: boolean; jwLink?: string; wolLink?: string }
interface Result { reference: string; section?: string; score: number; denseRrf: number; lexicalRrf: number; layers: string[]; verses: Verse[] }
interface Response { results: Result[]; citation?: { bookName: string; chapter: number; startVerse: number; endVerse: number }; degradedToLexicalOnly: boolean; cacheState: string; coverageStatus: string; mode: string }

@Component({ selector: 'app-root', imports: [CommonModule, FormsModule], templateUrl: './app.html', styleUrl: './app.css' })
export class App {
  private readonly http = inject(HttpClient);
  readonly api = (globalThis as any).CORPUSLENS_API_BASE_URL || '/api/v1';
  query = '';
  language = 'E';
  mode = 'Hybrid';
  response?: Response;
  loading = false;
  error = '';
  samples = ['John 3:16', 'Mateo 6:25-34', 'Who cares for me like a shepherd?', '¿Cómo puedo dejar de preocuparme?'];
  get citationBadge(): string { return /^[\p{L}. ]+\s+\d{1,3}:\d{1,3}(?:[-–]\d{1,3})?$/u.test(this.query) ? 'Citation detected' : ''; }
  search() {
    if (!this.query.trim() || this.loading) return;
    this.loading = true; this.error = '';
    this.http.post<Response>(`${this.api}/search`, { query: this.query, language: this.language, edition: null, limit: 10, mode: this.mode }).subscribe({
      next: response => { this.response = response; this.loading = false; },
      error: error => { this.error = error?.error?.error || 'Search is unavailable. Check the API connection.'; this.loading = false; }
    });
  }
  choose(sample: string) { this.query = sample; this.language = /[¿áéíóú]|Mateo/.test(sample) ? 'S' : 'E'; this.search(); }
}
