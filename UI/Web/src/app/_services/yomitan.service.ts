import {inject, Injectable} from '@angular/core';
import {environment} from "../../environments/environment";
import {AccountService} from "./account.service";

/**
 * The integration module served from assets/yomitan/js/kavita/kavita-yomitan.js. Yomitan's code is
 * loaded at runtime from assets, outside the Angular build.
 */
interface KavitaYomitan {
  enableScanning(details: {includeSelector: string, excludeSelector: string}): Promise<void>;
  disableScanning(): Promise<void>;
  isScannableTextAt(x: number, y: number): boolean;
  isPopupVisible(): boolean;
  hidePopup(): void;
  isScanSelection(selection: Selection | null): boolean;
  invoke<T = unknown>(action: string, params?: unknown): Promise<T>;
  request<T = unknown>(path: string, body?: unknown): Promise<T>;
  getDefaultOptions(): Promise<any>;
}

interface KavitaYomitanModule {
  start(config: {assetBaseUrl: string, apiUrl: string, getToken: () => string | null, settingsUrl: string}): Promise<KavitaYomitan>;
}

/**
 * Text inside the reader that Yomitan never scans. Links and highlights keep their own click
 * behavior.
 */
const READER_EXCLUDE_SELECTOR = 'a, a *, app-epub-highlight, app-epub-highlight *, [kavita-part], [kavita-part] *';

/**
 * Runs Yomitan (the pop-up dictionary) inside Kavita. Starting it loads Yomitan's background page
 * in a hidden frame, which then stays up for the rest of the session.
 */
@Injectable({
  providedIn: 'root'
})
export class YomitanService {

  private readonly accountService = inject(AccountService);

  private instancePromise: Promise<KavitaYomitan> | null = null;
  private instance: KavitaYomitan | null = null;
  private scanningEnabled = false;

  get assetBaseUrl() {
    return new URL('assets/yomitan', document.baseURI).href;
  }

  start(): Promise<KavitaYomitan> {
    if (this.instancePromise === null) {
      this.instancePromise = this.load();
      this.instancePromise.then(instance => this.instance = instance, () => this.instancePromise = null);
    }
    return this.instancePromise;
  }

  /**
   * Turns on click-to-look-up for text inside the element matching `contentSelector`.
   */
  async enableReaderScanning(contentSelector: string) {
    this.scanningEnabled = true;
    const instance = await this.start();
    if (!this.scanningEnabled) return;
    await instance.enableScanning({includeSelector: contentSelector, excludeSelector: READER_EXCLUDE_SELECTOR});
  }

  async disableReaderScanning() {
    this.scanningEnabled = false;
    if (this.instance === null) return;
    await this.instance.disableScanning();
  }

  /**
   * True when Yomitan is scanning and a click at this point will look up a word.
   */
  isLookupClick(event: MouseEvent) {
    if (!this.scanningEnabled || this.instance === null) return false;
    return this.instance.isScannableTextAt(event.clientX, event.clientY);
  }

  isPopupVisible() {
    return this.instance?.isPopupVisible() ?? false;
  }

  hidePopup() {
    this.instance?.hidePopup();
  }

  /**
   * True when the selection is the word Yomitan highlighted for its lookup, not a reader's own selection.
   */
  isScanSelection(selection: Selection | null) {
    if (!this.scanningEnabled || this.instance === null) return false;
    return this.instance.isScanSelection(selection);
  }

  async invoke<T = unknown>(action: string, params?: unknown) {
    const instance = await this.start();
    return await instance.invoke<T>(action, params);
  }

  async request<T = unknown>(path: string, body?: unknown) {
    const instance = await this.start();
    return await instance.request<T>(path, body);
  }

  async getDefaultOptions() {
    const instance = await this.start();
    return await instance.getDefaultOptions();
  }

  private async load() {
    const moduleUrl = `${this.assetBaseUrl}/js/kavita/kavita-yomitan.js`;
    const module: KavitaYomitanModule = await import(/* @vite-ignore */ moduleUrl);
    return await module.start({
      assetBaseUrl: this.assetBaseUrl,
      apiUrl: new URL(environment.apiUrl, document.baseURI).href,
      getToken: () => this.accountService.currentUser()?.token ?? null,
      settingsUrl: new URL('settings#yomitan', document.baseURI).href,
    });
  }
}
